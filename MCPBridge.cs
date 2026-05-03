#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Net;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[InitializeOnLoad]
public static class McpBridgeBootstrap
{
    static McpBridgeBootstrap()
    {
        AssemblyReloadEvents.beforeAssemblyReload += McpBridgeServer.Stop;
        EditorApplication.quitting += McpBridgeServer.Stop;
        EditorApplication.update += McpBridgeServer.PumpMainThread;

        if (EditorPrefs.GetBool(McpBridgeServer.AutoStartPrefKey, false))
        {
            McpBridgeServer.Start();
        }
    }
}

public static class McpBridgeServer
{
    public const string AutoStartPrefKey = "MCPBridge.AutoStart";
    public const string TokenPrefKey = "MCPBridge.Token";
    public const string PortPrefKey = "MCPBridge.Port";
    private const int DefaultPort = 8080;
    private const int MainThreadTimeoutMs = 10000;

    private static HttpListener _listener;
    private static volatile bool _isRunning;
    private static int _port = DefaultPort;
    private static string _token;
    private static readonly Queue<Action> _commandQueue = new Queue<Action>();
    private static readonly object _queueLock = new object();

    public static bool IsRunning => _isRunning;
    public static int Port => _port;
    public static string Token => _token;

    public static void Start()
    {
        if (_isRunning) return;

        _port = EditorPrefs.GetInt(PortPrefKey, DefaultPort);
        _token = EditorPrefs.GetString(TokenPrefKey, "");
        if (string.IsNullOrEmpty(_token))
        {
            _token = GenerateToken();
            EditorPrefs.SetString(TokenPrefKey, _token);
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

        try
        {
            _listener.Start();
            _isRunning = true;
            Debug.Log($"[MCP] Listening on http://127.0.0.1:{_port} (token saved in EditorPrefs)");
            _listener.BeginGetContext(HandleRequest, null);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MCP] Failed to start: {e.Message}");
            _listener = null;
            _isRunning = false;
        }
    }

    public static void Stop()
    {
        if (!_isRunning && _listener == null) return;

        _isRunning = false;
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;

        lock (_queueLock) { _commandQueue.Clear(); }
        Debug.Log("[MCP] Server stopped.");
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    public static void RegenerateToken()
    {
        _token = GenerateToken();
        EditorPrefs.SetString(TokenPrefKey, _token);
        Debug.Log("[MCP] Token regenerated.");
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    public static void PumpMainThread()
    {
        if (_commandQueue.Count == 0) return;

        while (true)
        {
            Action next;
            lock (_queueLock)
            {
                if (_commandQueue.Count == 0) return;
                next = _commandQueue.Dequeue();
            }
            try { next?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    private static void HandleRequest(IAsyncResult result)
    {
        if (!_isRunning || _listener == null) return;

        HttpListenerContext context = null;
        try { context = _listener.EndGetContext(result); }
        catch (ObjectDisposedException) { return; }
        catch (HttpListenerException) { return; }
        catch (Exception e) { Debug.LogException(e); }
        finally
        {
            if (_isRunning && _listener != null)
            {
                try { _listener.BeginGetContext(HandleRequest, null); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        if (context != null)
        {
            ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
        }
    }

    private static void ProcessRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // DNS-rebinding guard: only accept requests addressed to localhost.
            var host = request.Headers["Host"] ?? "";
            if (!host.StartsWith("127.0.0.1") && !host.StartsWith("localhost"))
            {
                WriteJson(response, 403, new { error = "Forbidden host" });
                return;
            }

            // Auth.
            var token = request.Headers["X-MCP-Token"];
            if (string.IsNullOrEmpty(_token) || token != _token)
            {
                WriteJson(response, 401, new { error = "Unauthorized" });
                return;
            }

            if (request.HttpMethod != "POST" && request.HttpMethod != "GET")
            {
                WriteJson(response, 405, new { error = "Method not allowed" });
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            string path = request.Url.AbsolutePath;
            string output = null;
            Exception failure = null;

            using (var done = new ManualResetEventSlim(false))
            {
                lock (_queueLock)
                {
                    _commandQueue.Enqueue(() =>
                    {
                        try { output = Dispatch(path, body); }
                        catch (Exception ex) { failure = ex; }
                        finally { done.Set(); }
                    });
                }

                if (!done.Wait(MainThreadTimeoutMs))
                {
                    WriteJson(response, 504, new { error = "Unity main-thread timeout" });
                    return;
                }
            }

            if (failure != null)
            {
                WriteJson(response, 500, new { error = failure.Message });
                return;
            }

            WriteRawJson(response, 200, output ?? "{}");
        }
        catch (Exception e)
        {
            try { WriteJson(response, 500, new { error = e.Message }); }
            catch { /* connection already gone */ }
        }
        finally
        {
            try { response.Close(); } catch { /* ignore */ }
        }
    }

    private static void WriteJson(HttpListenerResponse response, int status, object payload)
    {
        WriteRawJson(response, status, JsonConvert.SerializeObject(payload));
    }

    private static void WriteRawJson(HttpListenerResponse response, int status, string json)
    {
        var buffer = System.Text.Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
    }

    private static string Dispatch(string path, string json)
    {
        var data = string.IsNullOrEmpty(json) ? new JObject() : JObject.Parse(json);
        switch (path)
        {
            case "/ping":                   return JsonConvert.SerializeObject(new { ok = true });
            case "/create_object":          return McpCommands.CreateObject(data);
            case "/delete_object":          return McpCommands.DeleteObject(data);
            case "/set_transform":          return McpCommands.SetTransform(data);
            case "/get_object_info":        return McpCommands.GetObjectInfo(data);
            case "/set_component_property": return McpCommands.SetComponentProperty(data);
            case "/instantiate_prefab":     return McpCommands.InstantiatePrefab(data);
            case "/list_scene_objects":     return McpCommands.ListSceneObjects();
            default: return JsonConvert.SerializeObject(new { error = $"Unknown path: {path}" });
        }
    }
}

internal static class McpCommands
{
    public static string CreateObject(JObject data)
    {
        string type = (string)data["type"] ?? "Cube";
        string name = (string)data["name"] ?? type;

        GameObject obj = type switch
        {
            "Cube"     => GameObject.CreatePrimitive(PrimitiveType.Cube),
            "Sphere"   => GameObject.CreatePrimitive(PrimitiveType.Sphere),
            "Plane"    => GameObject.CreatePrimitive(PrimitiveType.Plane),
            "Cylinder" => GameObject.CreatePrimitive(PrimitiveType.Cylinder),
            "Capsule"  => GameObject.CreatePrimitive(PrimitiveType.Capsule),
            "Quad"     => GameObject.CreatePrimitive(PrimitiveType.Quad),
            "Empty"    => new GameObject(),
            _          => null
        };

        if (obj == null)
        {
            return JsonConvert.SerializeObject(new { error = $"Unknown primitive type: {type}" });
        }

        obj.name = name;
        Undo.RegisterCreatedObjectUndo(obj, $"MCP Create {name}");
        EditorSceneManager.MarkSceneDirty(obj.scene);

        return JsonConvert.SerializeObject(new
        {
            id = obj.GetInstanceID(),
            name = obj.name,
            path = GetPath(obj)
        });
    }

    public static string DeleteObject(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;
        Undo.DestroyObjectImmediate(obj);
        return JsonConvert.SerializeObject(new { success = true });
    }

    public static string SetTransform(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;

        Undo.RecordObject(obj.transform, "MCP SetTransform");

        if (data.TryGetValue("position", out var pos) && pos is JObject posObj)
            obj.transform.position = ToVector3(posObj);
        if (data.TryGetValue("rotation", out var rot) && rot is JObject rotObj)
            obj.transform.eulerAngles = ToVector3(rotObj);
        if (data.TryGetValue("scale", out var scl) && scl is JObject sclObj)
            obj.transform.localScale = ToVector3(sclObj);

        EditorUtility.SetDirty(obj);
        EditorSceneManager.MarkSceneDirty(obj.scene);

        return JsonConvert.SerializeObject(new { success = true });
    }

    public static string SetComponentProperty(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;

        string componentName = (string)data["component"];
        string propertyName = (string)data["property"];
        var valueToken = data["value"];

        if (string.IsNullOrEmpty(componentName) || string.IsNullOrEmpty(propertyName))
            return JsonConvert.SerializeObject(new { error = "component and property are required" });
        if (valueToken == null)
            return JsonConvert.SerializeObject(new { error = "value is required" });

        var component = obj.GetComponent(componentName);
        if (component == null)
            return JsonConvert.SerializeObject(new { error = $"Component '{componentName}' not found on object" });

        var type = component.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        Undo.RecordObject(component, "MCP SetComponentProperty");

        var field = type.GetField(propertyName, flags);
        if (field != null)
        {
            try
            {
                field.SetValue(component, valueToken.ToObject(field.FieldType));
                EditorUtility.SetDirty(component);
                return JsonConvert.SerializeObject(new { success = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = $"Cannot assign field: {ex.Message}" });
            }
        }

        var prop = type.GetProperty(propertyName, flags);
        if (prop != null && prop.CanWrite)
        {
            try
            {
                prop.SetValue(component, valueToken.ToObject(prop.PropertyType));
                EditorUtility.SetDirty(component);
                return JsonConvert.SerializeObject(new { success = true });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = $"Cannot assign property: {ex.Message}" });
            }
        }

        return JsonConvert.SerializeObject(new { error = $"Public field/property '{propertyName}' not found on '{componentName}'" });
    }

    public static string GetObjectInfo(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;

        var components = new List<string>();
        foreach (var c in obj.GetComponents<Component>())
        {
            if (c != null) components.Add(c.GetType().Name);
        }

        return JsonConvert.SerializeObject(new
        {
            id = obj.GetInstanceID(),
            name = obj.name,
            path = GetPath(obj),
            active = obj.activeInHierarchy,
            tag = obj.tag,
            layer = LayerMask.LayerToName(obj.layer),
            position = ToJson(obj.transform.position),
            rotation = ToJson(obj.transform.eulerAngles),
            scale = ToJson(obj.transform.localScale),
            components
        });
    }

    public static string InstantiatePrefab(JObject data)
    {
        string assetPath = (string)data["path"];
        if (string.IsNullOrEmpty(assetPath))
            return JsonConvert.SerializeObject(new { error = "path is required (e.g. 'Assets/Prefabs/Foo.prefab')" });

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null)
            return JsonConvert.SerializeObject(new { error = $"Prefab not found at '{assetPath}'" });

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        if (instance == null)
            return JsonConvert.SerializeObject(new { error = "InstantiatePrefab returned null" });

        if (data.TryGetValue("name", out var n) && n.Type == JTokenType.String)
            instance.name = (string)n;

        Undo.RegisterCreatedObjectUndo(instance, $"MCP Instantiate {instance.name}");
        EditorSceneManager.MarkSceneDirty(instance.scene);

        return JsonConvert.SerializeObject(new
        {
            id = instance.GetInstanceID(),
            name = instance.name,
            path = GetPath(instance)
        });
    }

    public static string ListSceneObjects()
    {
        var objects = new List<object>();
        foreach (var r in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            objects.Add(new { id = r.GetInstanceID(), name = r.name, path = GetPath(r) });
        }
        return JsonConvert.SerializeObject(new { objects });
    }

    // -- helpers --

    private static bool TryGetGameObject(JObject data, out GameObject obj, out string errorJson)
    {
        obj = null;
        errorJson = null;

        var idToken = data["id"];
        if (idToken == null || idToken.Type == JTokenType.Null)
        {
            errorJson = JsonConvert.SerializeObject(new { error = "id is required" });
            return false;
        }

        int id;
        try { id = idToken.ToObject<int>(); }
        catch
        {
            errorJson = JsonConvert.SerializeObject(new { error = "id must be an integer" });
            return false;
        }

        obj = EditorUtility.InstanceIDToObject(id) as GameObject;
        if (obj == null)
        {
            errorJson = JsonConvert.SerializeObject(new { error = $"GameObject with id {id} not found" });
            return false;
        }
        return true;
    }

    private static Vector3 ToVector3(JObject o) => new Vector3(
        o["x"]?.ToObject<float>() ?? 0f,
        o["y"]?.ToObject<float>() ?? 0f,
        o["z"]?.ToObject<float>() ?? 0f);

    private static object ToJson(Vector3 v) => new { x = v.x, y = v.y, z = v.z };

    private static string GetPath(GameObject obj) =>
        obj.transform.parent == null
            ? obj.name
            : GetPath(obj.transform.parent.gameObject) + "/" + obj.name;
}

public class McpBridgeWindow : EditorWindow
{
    [MenuItem("Tools/MCP Bridge/Control Panel")]
    public static void ShowWindow() => GetWindow<McpBridgeWindow>("MCP Bridge");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Unity ↔ Claude Code MCP Bridge", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Status:", McpBridgeServer.IsRunning ? "RUNNING" : "STOPPED");
        EditorGUILayout.LabelField("Port:", McpBridgeServer.Port.ToString());

        EditorGUI.BeginChangeCheck();
        int port = EditorGUILayout.IntField("Configured Port",
            EditorPrefs.GetInt(McpBridgeServer.PortPrefKey, 8080));
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetInt(McpBridgeServer.PortPrefKey, Mathf.Clamp(port, 1024, 65535));
        }

        bool autoStart = EditorPrefs.GetBool(McpBridgeServer.AutoStartPrefKey, false);
        bool newAuto = EditorGUILayout.Toggle("Auto-start on load", autoStart);
        if (newAuto != autoStart) EditorPrefs.SetBool(McpBridgeServer.AutoStartPrefKey, newAuto);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Auth token (share with the Python MCP server):");
        EditorGUILayout.SelectableLabel(McpBridgeServer.Token ?? "(generated on first start)",
            EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = !McpBridgeServer.IsRunning;
            if (GUILayout.Button("Start")) McpBridgeServer.Start();
            GUI.enabled = McpBridgeServer.IsRunning;
            if (GUILayout.Button("Stop")) McpBridgeServer.Stop();
            GUI.enabled = true;
            if (GUILayout.Button("Restart")) McpBridgeServer.Restart();
            if (GUILayout.Button("Regenerate Token")) McpBridgeServer.RegenerateToken();
        }
    }

    private void OnInspectorUpdate() => Repaint();
}
#endif
