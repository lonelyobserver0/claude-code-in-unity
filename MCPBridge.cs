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
        McpLogBuffer.Register();

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
            case "/add_component":          return McpCommands.AddComponent(data);
            case "/remove_component":       return McpCommands.RemoveComponent(data);
            case "/find_object":            return McpCommands.FindObject(data);
            case "/get_children":           return McpCommands.GetChildren(data);
            case "/set_parent":             return McpCommands.SetParent(data);
            case "/set_active":             return McpCommands.SetActive(data);
            case "/set_tag":                return McpCommands.SetTag(data);
            case "/set_layer":              return McpCommands.SetLayer(data);
            case "/save_scene":             return McpCommands.SaveScene(data);
            case "/open_scene":             return McpCommands.OpenScene(data);
            case "/new_scene":              return McpCommands.NewScene(data);
            case "/get_scene_info":         return McpCommands.GetSceneInfo();
            case "/select_object":          return McpCommands.SelectObject(data);
            case "/execute_menu_item":      return McpCommands.ExecuteMenuItem(data);
            case "/set_play_mode":          return McpCommands.SetPlayMode(data);
            case "/get_play_mode":          return McpCommands.GetPlayMode();
            case "/get_console_logs":       return McpCommands.GetConsoleLogs(data);
            case "/clear_console_logs":     return McpCommands.ClearConsoleLogs();
            case "/import_asset":           return McpCommands.ImportAsset(data);
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
            id = GetId(obj),
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
            id = GetId(obj),
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
            id = GetId(instance),
            name = instance.name,
            path = GetPath(instance)
        });
    }

    public static string ListSceneObjects()
    {
        var objects = new List<object>();
        foreach (var r in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            objects.Add(new { id = GetId(r), name = r.name, path = GetPath(r) });
        }
        return JsonConvert.SerializeObject(new { objects });
    }

    public static string AddComponent(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;

        string componentName = (string)data["component"];
        if (string.IsNullOrEmpty(componentName))
            return JsonConvert.SerializeObject(new { error = "component is required" });

        var type = ResolveComponentType(componentName);
        if (type == null)
            return JsonConvert.SerializeObject(new { error = $"Component type '{componentName}' not found in any loaded assembly" });

        Component added;
        try { added = Undo.AddComponent(obj, type); }
        catch (Exception ex) { return JsonConvert.SerializeObject(new { error = $"AddComponent failed: {ex.Message}" }); }

        if (added == null)
            return JsonConvert.SerializeObject(new { error = $"AddComponent returned null for '{componentName}'" });

        EditorUtility.SetDirty(obj);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return JsonConvert.SerializeObject(new { success = true, component = added.GetType().Name });
    }

    public static string RemoveComponent(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;

        string componentName = (string)data["component"];
        if (string.IsNullOrEmpty(componentName))
            return JsonConvert.SerializeObject(new { error = "component is required" });

        var component = obj.GetComponent(componentName);
        if (component == null)
            return JsonConvert.SerializeObject(new { error = $"Component '{componentName}' not found on object" });
        if (component is Transform)
            return JsonConvert.SerializeObject(new { error = "Transform cannot be removed" });

        Undo.DestroyObjectImmediate(component);
        EditorUtility.SetDirty(obj);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return JsonConvert.SerializeObject(new { success = true });
    }

    public static string FindObject(JObject data)
    {
        string path = (string)data["path"];
        string name = (string)data["name"];

        GameObject obj = null;
        if (!string.IsNullOrEmpty(path))
        {
            obj = GameObject.Find(path);
        }
        else if (!string.IsNullOrEmpty(name))
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                obj = FindByNameRecursive(root.transform, name);
                if (obj != null) break;
            }
        }
        else
        {
            return JsonConvert.SerializeObject(new { error = "name or path is required" });
        }

        if (obj == null)
            return JsonConvert.SerializeObject(new { error = "GameObject not found" });

        return JsonConvert.SerializeObject(new
        {
            id = GetId(obj),
            name = obj.name,
            path = GetPath(obj)
        });
    }

    public static string GetChildren(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;

        var children = new List<object>();
        foreach (Transform child in obj.transform)
        {
            children.Add(new
            {
                id = GetId(child.gameObject),
                name = child.gameObject.name,
                path = GetPath(child.gameObject)
            });
        }
        return JsonConvert.SerializeObject(new { children });
    }

    public static string SetParent(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;

        Transform newParent = null;
        if (data.TryGetValue("parent_id", out var parentToken) && parentToken.Type != JTokenType.Null)
        {
            ulong parentId;
            try { parentId = parentToken.ToObject<ulong>(); }
            catch { return JsonConvert.SerializeObject(new { error = "parent_id must be an integer" }); }

            var parentGo = IdToGameObject(parentId);
            if (parentGo == null)
                return JsonConvert.SerializeObject(new { error = $"Parent GameObject with id {parentId} not found" });
            newParent = parentGo.transform;
        }

        bool worldPositionStays = data["world_position_stays"]?.ToObject<bool>() ?? true;

        Undo.SetTransformParent(obj.transform, newParent, "MCP SetParent");
        if (!worldPositionStays)
        {
            Undo.RecordObject(obj.transform, "MCP SetParent (reset local)");
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
        }

        EditorSceneManager.MarkSceneDirty(obj.scene);
        return JsonConvert.SerializeObject(new { success = true, path = GetPath(obj) });
    }

    public static string SetActive(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;
        var activeToken = data["active"];
        if (activeToken == null)
            return JsonConvert.SerializeObject(new { error = "active is required (bool)" });

        bool active;
        try { active = activeToken.ToObject<bool>(); }
        catch { return JsonConvert.SerializeObject(new { error = "active must be a boolean" }); }

        Undo.RecordObject(obj, "MCP SetActive");
        obj.SetActive(active);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return JsonConvert.SerializeObject(new { success = true, active = obj.activeSelf });
    }

    public static string SetTag(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;
        string tag = (string)data["tag"];
        if (string.IsNullOrEmpty(tag))
            return JsonConvert.SerializeObject(new { error = "tag is required" });

        try
        {
            Undo.RecordObject(obj, "MCP SetTag");
            obj.tag = tag;
            EditorSceneManager.MarkSceneDirty(obj.scene);
            return JsonConvert.SerializeObject(new { success = true });
        }
        catch (UnityException ex)
        {
            return JsonConvert.SerializeObject(new { error = $"Tag '{tag}' is not defined: {ex.Message}" });
        }
    }

    public static string SetLayer(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;
        var layerToken = data["layer"];
        if (layerToken == null)
            return JsonConvert.SerializeObject(new { error = "layer is required (int 0-31 or string name)" });

        int layer;
        if (layerToken.Type == JTokenType.String)
        {
            layer = LayerMask.NameToLayer((string)layerToken);
            if (layer < 0)
                return JsonConvert.SerializeObject(new { error = $"Layer name '{(string)layerToken}' not found" });
        }
        else
        {
            try { layer = layerToken.ToObject<int>(); }
            catch { return JsonConvert.SerializeObject(new { error = "layer must be int or string" }); }
            if (layer < 0 || layer > 31)
                return JsonConvert.SerializeObject(new { error = "layer index must be between 0 and 31" });
        }

        Undo.RecordObject(obj, "MCP SetLayer");
        obj.layer = layer;
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return JsonConvert.SerializeObject(new { success = true, layer, name = LayerMask.LayerToName(layer) });
    }

    public static string SaveScene(JObject data)
    {
        var scene = SceneManager.GetActiveScene();
        string path = (string)data["path"];
        bool ok = string.IsNullOrEmpty(path)
            ? EditorSceneManager.SaveScene(scene)
            : EditorSceneManager.SaveScene(scene, path);

        return JsonConvert.SerializeObject(new { success = ok, path = scene.path });
    }

    public static string OpenScene(JObject data)
    {
        string path = (string)data["path"];
        if (string.IsNullOrEmpty(path))
            return JsonConvert.SerializeObject(new { error = "path is required (e.g. 'Assets/Scenes/Main.unity')" });

        string modeStr = (string)data["mode"] ?? "Single";
        OpenSceneMode mode;
        switch (modeStr)
        {
            case "Single":                  mode = OpenSceneMode.Single; break;
            case "Additive":                mode = OpenSceneMode.Additive; break;
            case "AdditiveWithoutLoading":  mode = OpenSceneMode.AdditiveWithoutLoading; break;
            default: return JsonConvert.SerializeObject(new { error = $"Unknown mode: {modeStr} (Single|Additive|AdditiveWithoutLoading)" });
        }

        try
        {
            var scene = EditorSceneManager.OpenScene(path, mode);
            return JsonConvert.SerializeObject(new { success = scene.IsValid(), name = scene.name, path = scene.path });
        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject(new { error = ex.Message });
        }
    }

    public static string NewScene(JObject data)
    {
        string setupStr = (string)data["setup"] ?? "DefaultGameObjects";
        NewSceneSetup setup = setupStr == "EmptyScene" ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects;

        try
        {
            var scene = EditorSceneManager.NewScene(setup, NewSceneMode.Single);
            return JsonConvert.SerializeObject(new { success = scene.IsValid(), name = scene.name });
        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject(new { error = ex.Message });
        }
    }

    public static string GetSceneInfo()
    {
        var scene = SceneManager.GetActiveScene();
        return JsonConvert.SerializeObject(new
        {
            name = scene.name,
            path = scene.path,
            is_dirty = scene.isDirty,
            is_loaded = scene.isLoaded,
            root_count = scene.rootCount,
            build_index = scene.buildIndex
        });
    }

    public static string SelectObject(JObject data)
    {
        if (!TryGetGameObject(data, out var obj, out var error)) return error;
        bool frame = data["frame"]?.ToObject<bool>() ?? false;

        Selection.activeGameObject = obj;
        EditorGUIUtility.PingObject(obj);
        if (frame && SceneView.lastActiveSceneView != null)
        {
            SceneView.lastActiveSceneView.FrameSelected();
        }
        return JsonConvert.SerializeObject(new { success = true });
    }

    public static string ExecuteMenuItem(JObject data)
    {
        string menuPath = (string)data["menu_path"];
        if (string.IsNullOrEmpty(menuPath))
            return JsonConvert.SerializeObject(new { error = "menu_path is required (e.g. 'GameObject/3D Object/Cube')" });

        bool ok = EditorApplication.ExecuteMenuItem(menuPath);
        return JsonConvert.SerializeObject(new { success = ok });
    }

    public static string SetPlayMode(JObject data)
    {
        string state = (string)data["state"];
        if (string.IsNullOrEmpty(state))
            return JsonConvert.SerializeObject(new { error = "state is required: 'play' | 'stop' | 'pause' | 'unpause'" });

        switch (state.ToLowerInvariant())
        {
            case "play":    EditorApplication.EnterPlaymode(); break;
            case "stop":    EditorApplication.ExitPlaymode();  break;
            case "pause":   EditorApplication.isPaused = true; break;
            case "unpause": EditorApplication.isPaused = false; break;
            default: return JsonConvert.SerializeObject(new { error = $"Unknown state '{state}' (play|stop|pause|unpause)" });
        }
        return JsonConvert.SerializeObject(new { success = true });
    }

    public static string GetPlayMode()
    {
        return JsonConvert.SerializeObject(new
        {
            is_playing   = EditorApplication.isPlaying,
            is_paused    = EditorApplication.isPaused,
            is_compiling = EditorApplication.isCompiling,
            is_updating  = EditorApplication.isUpdating
        });
    }

    public static string GetConsoleLogs(JObject data)
    {
        int limit = data["limit"]?.ToObject<int>() ?? 100;
        string severity = (string)data["severity"];
        var entries = McpLogBuffer.Snapshot(limit, severity);
        return JsonConvert.SerializeObject(new { logs = entries });
    }

    public static string ClearConsoleLogs()
    {
        McpLogBuffer.Clear();
        return JsonConvert.SerializeObject(new { success = true });
    }

    public static string ImportAsset(JObject data)
    {
        string src = (string)data["src_path"];
        string dst = (string)data["dst_path"];
        bool overwrite = data["overwrite"]?.ToObject<bool>() ?? false;

        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
            return JsonConvert.SerializeObject(new { error = "src_path and dst_path are required" });
        if (!File.Exists(src))
            return JsonConvert.SerializeObject(new { error = $"Source file not found: {src}" });

        string dstNorm = dst.Replace('\\', '/').TrimStart('/');
        if (!dstNorm.StartsWith("Assets/") && dstNorm != "Assets")
            return JsonConvert.SerializeObject(new { error = "dst_path must be project-relative and start with 'Assets/'" });

        // Resolve to an absolute path under the project's Assets/ and refuse anything that escapes it.
        string projectAssets = Path.GetFullPath(Application.dataPath);
        string projectRoot = Path.GetFullPath(Path.Combine(projectAssets, ".."));
        string fullDst = Path.GetFullPath(Path.Combine(projectRoot, dstNorm));
        if (!fullDst.StartsWith(projectAssets, StringComparison.Ordinal))
            return JsonConvert.SerializeObject(new { error = "dst_path escapes the Assets folder" });

        if (File.Exists(fullDst) && !overwrite)
            return JsonConvert.SerializeObject(new { error = $"Destination already exists at '{dstNorm}' (pass overwrite=true to replace)" });

        try
        {
            string parent = Path.GetDirectoryName(fullDst);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(src, fullDst, overwrite);
            AssetDatabase.ImportAsset(dstNorm, ImportAssetOptions.ForceUpdate);
            return JsonConvert.SerializeObject(new { success = true, path = dstNorm });
        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject(new { error = $"Import failed: {ex.Message}" });
        }
    }

    // -- helpers --

    // EntityId (Unity 6.3+) replaces InstanceID. The bridge uses ulong as transport so
    // both branches share the same JSON shape; clients keep passing/receiving a number.
    private static ulong GetId(UnityEngine.Object obj)
    {
#if UNITY_6000_3_OR_NEWER
        return EntityId.ToULong(obj.GetEntityId());
#else
        return unchecked((ulong)(uint)obj.GetInstanceID());
#endif
    }

    private static GameObject IdToGameObject(ulong id)
    {
#if UNITY_6000_3_OR_NEWER
        return EditorUtility.EntityIdToObject(EntityId.FromULong(id)) as GameObject;
#else
        return EditorUtility.InstanceIDToObject(unchecked((int)(uint)id)) as GameObject;
#endif
    }

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

        ulong id;
        try { id = idToken.ToObject<ulong>(); }
        catch
        {
            errorJson = JsonConvert.SerializeObject(new { error = "id must be an integer" });
            return false;
        }

        obj = IdToGameObject(id);
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

    private static GameObject FindByNameRecursive(Transform t, string name)
    {
        if (t.gameObject.name == name) return t.gameObject;
        for (int i = 0; i < t.childCount; i++)
        {
            var found = FindByNameRecursive(t.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static Type ResolveComponentType(string name)
    {
        var direct = Type.GetType(name);
        if (direct != null && typeof(Component).IsAssignableFrom(direct)) return direct;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (!typeof(Component).IsAssignableFrom(t)) continue;
                if (t.Name == name || t.FullName == name) return t;
            }
        }
        return null;
    }
}

internal static class McpLogBuffer
{
    private const int MaxEntries = 1000;

    public class LogEntry
    {
        public string time;
        public string type;
        public string message;
        public string stack;
    }

    private static readonly LinkedList<LogEntry> _entries = new LinkedList<LogEntry>();
    private static readonly object _lock = new object();
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        Application.logMessageReceivedThreaded += OnLog;
        _registered = true;
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        var entry = new LogEntry
        {
            time = DateTime.UtcNow.ToString("o"),
            type = type.ToString(),
            message = condition,
            stack = stackTrace
        };
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxEntries) _entries.RemoveFirst();
        }
    }

    public static List<LogEntry> Snapshot(int limit, string severity)
    {
        var result = new List<LogEntry>();
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                if (!string.IsNullOrEmpty(severity) &&
                    !string.Equals(e.type, severity, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(e);
            }
        }
        if (limit > 0 && result.Count > limit)
        {
            result.RemoveRange(0, result.Count - limit);
        }
        return result;
    }

    public static void Clear()
    {
        lock (_lock) { _entries.Clear(); }
    }
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
