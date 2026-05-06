#!/usr/bin/env python3
"""MCP server bridging Claude Code to the Unity Editor over loopback HTTP.

Pairs with `MCPBridge.cs` (Unity Editor side). Reads connection settings from
environment variables so the same script can target different Unity instances
without code changes:

    UNITY_MCP_URL      base URL of the Unity bridge   (default: http://127.0.0.1:8080)
    UNITY_MCP_TOKEN    shared auth token              (required; copy from Unity panel)
    UNITY_MCP_TIMEOUT  per-request timeout in seconds (default: 10.0)
"""
from __future__ import annotations

import asyncio
import os
from typing import Any

import httpx
from mcp.server.fastmcp import FastMCP

UNITY_URL = os.environ.get("UNITY_MCP_URL", "http://127.0.0.1:8080").rstrip("/")
UNITY_TOKEN = os.environ.get("UNITY_MCP_TOKEN", "")
UNITY_TIMEOUT = float(os.environ.get("UNITY_MCP_TIMEOUT", "10.0"))

mcp = FastMCP("Unity Editor Bridge")


async def _send(path: str, data: dict[str, Any] | None = None) -> dict[str, Any]:
    if not UNITY_TOKEN:
        return {"error": "UNITY_MCP_TOKEN is not set. Copy the token from Unity > Tools > MCP Bridge > Control Panel."}

    headers = {"X-MCP-Token": UNITY_TOKEN, "Content-Type": "application/json"}
    try:
        async with httpx.AsyncClient(timeout=UNITY_TIMEOUT) as client:
            response = await client.post(
                f"{UNITY_URL}{path}",
                json=data or {},
                headers=headers,
            )
    except httpx.TimeoutException:
        return {"error": f"Unity bridge timed out after {UNITY_TIMEOUT}s on {path}"}
    except httpx.RequestError as e:
        return {"error": f"Connection failed: {e}"}

    if response.status_code == 401:
        return {"error": "Unauthorized: token mismatch between this server and Unity."}
    if response.status_code >= 400:
        try:
            return response.json()
        except ValueError:
            return {"error": f"HTTP {response.status_code}: {response.text}"}

    try:
        return response.json()
    except ValueError:
        return {"error": f"Invalid JSON from Unity: {response.text[:200]}"}


@mcp.tool()
async def ping() -> dict[str, Any]:
    """Health check: verify the Unity bridge is reachable and the token is correct."""
    return await _send("/ping")


@mcp.tool()
async def create_object(type: str = "Cube", name: str = "NewObject") -> dict[str, Any]:
    """Create a primitive in the active scene.

    type: Cube | Sphere | Plane | Cylinder | Capsule | Quad | Empty
    """
    return await _send("/create_object", {"type": type, "name": name})


@mcp.tool()
async def delete_object(object_id: int) -> dict[str, Any]:
    """Delete a GameObject from the scene by its instance id (undoable in Unity)."""
    return await _send("/delete_object", {"id": object_id})


@mcp.tool()
async def move_object(
    object_id: int,
    position: dict[str, float] | None = None,
    rotation: dict[str, float] | None = None,
    scale: dict[str, float] | None = None,
) -> dict[str, Any]:
    """Set transform values on a GameObject. Each arg uses {"x":..,"y":..,"z":..}."""
    payload: dict[str, Any] = {"id": object_id}
    if position is not None:
        payload["position"] = position
    if rotation is not None:
        payload["rotation"] = rotation
    if scale is not None:
        payload["scale"] = scale
    return await _send("/set_transform", payload)


@mcp.tool()
async def set_component_property(
    object_id: int,
    component: str,
    property: str,
    value: Any,
) -> dict[str, Any]:
    """Assign a public field or property on a Component via reflection.

    Examples:
        set_component_property(id, "Rigidbody", "mass", 2.5)
        set_component_property(id, "Light", "color", {"r":1,"g":0,"b":0,"a":1})
    """
    return await _send(
        "/set_component_property",
        {"id": object_id, "component": component, "property": property, "value": value},
    )


@mcp.tool()
async def get_object_info(object_id: int) -> dict[str, Any]:
    """Return name, path, transform, tag, layer, and component list for a GameObject."""
    return await _send("/get_object_info", {"id": object_id})


@mcp.tool()
async def list_scene_objects() -> dict[str, Any]:
    """List all root GameObjects in the active scene."""
    return await _send("/list_scene_objects")


@mcp.tool()
async def instantiate_prefab(path: str, name: str | None = None) -> dict[str, Any]:
    """Instantiate a prefab from the project's AssetDatabase.

    path: project-relative asset path, e.g. 'Assets/Prefabs/Enemy.prefab'.
    """
    payload: dict[str, Any] = {"path": path}
    if name is not None:
        payload["name"] = name
    return await _send("/instantiate_prefab", payload)


@mcp.tool()
async def add_component(object_id: int, component: str) -> dict[str, Any]:
    """Add a Component to a GameObject by type name (e.g. 'Rigidbody', 'BoxCollider', 'UnityEngine.Light').

    Resolves the type across all loaded assemblies, so MonoBehaviour subclasses also work.
    """
    return await _send("/add_component", {"id": object_id, "component": component})


@mcp.tool()
async def remove_component(object_id: int, component: str) -> dict[str, Any]:
    """Remove a Component from a GameObject by type name. Transform cannot be removed."""
    return await _send("/remove_component", {"id": object_id, "component": component})


@mcp.tool()
async def find_object(name: str | None = None, path: str | None = None) -> dict[str, Any]:
    """Locate a GameObject in the active scene.

    Pass either `path` (slash-separated hierarchy, e.g. 'Player/Body/Head')
    or `name` (recursive name match starting from scene roots).
    """
    payload: dict[str, Any] = {}
    if path is not None:
        payload["path"] = path
    if name is not None:
        payload["name"] = name
    return await _send("/find_object", payload)


@mcp.tool()
async def get_children(object_id: int) -> dict[str, Any]:
    """List the direct children of a GameObject (id, name, hierarchy path)."""
    return await _send("/get_children", {"id": object_id})


@mcp.tool()
async def set_parent(
    object_id: int,
    parent_id: int | None = None,
    world_position_stays: bool = True,
) -> dict[str, Any]:
    """Reparent a GameObject. Pass `parent_id=None` to detach to scene root.

    world_position_stays=False resets local transform to identity after reparenting.
    """
    payload: dict[str, Any] = {"id": object_id, "world_position_stays": world_position_stays}
    if parent_id is not None:
        payload["parent_id"] = parent_id
    return await _send("/set_parent", payload)


@mcp.tool()
async def set_active(object_id: int, active: bool) -> dict[str, Any]:
    """Enable/disable a GameObject (equivalent to ticking the checkbox in the Inspector)."""
    return await _send("/set_active", {"id": object_id, "active": active})


@mcp.tool()
async def set_tag(object_id: int, tag: str) -> dict[str, Any]:
    """Set the tag of a GameObject. The tag must already be defined in TagManager."""
    return await _send("/set_tag", {"id": object_id, "tag": tag})


@mcp.tool()
async def set_layer(object_id: int, layer: int | str) -> dict[str, Any]:
    """Set the layer of a GameObject. Accepts an int (0-31) or a layer name."""
    return await _send("/set_layer", {"id": object_id, "layer": layer})


@mcp.tool()
async def save_scene(path: str | None = None) -> dict[str, Any]:
    """Save the active scene. If `path` is provided, save-as to that asset path."""
    payload: dict[str, Any] = {}
    if path is not None:
        payload["path"] = path
    return await _send("/save_scene", payload)


@mcp.tool()
async def open_scene(path: str, mode: str = "Single") -> dict[str, Any]:
    """Open a scene from disk.

    mode: 'Single' | 'Additive' | 'AdditiveWithoutLoading'.
    Note: unsaved changes in the current scene are NOT auto-saved.
    """
    return await _send("/open_scene", {"path": path, "mode": mode})


@mcp.tool()
async def new_scene(setup: str = "DefaultGameObjects") -> dict[str, Any]:
    """Create a new scene, replacing the current one.

    setup: 'DefaultGameObjects' (with default camera+light) | 'EmptyScene'.
    """
    return await _send("/new_scene", {"setup": setup})


@mcp.tool()
async def get_scene_info() -> dict[str, Any]:
    """Return name, path, dirty flag, root count, and build index of the active scene."""
    return await _send("/get_scene_info")


@mcp.tool()
async def select_object(object_id: int, frame: bool = False) -> dict[str, Any]:
    """Select a GameObject in the Hierarchy and ping it. If `frame=True`, frame it in the Scene view."""
    return await _send("/select_object", {"id": object_id, "frame": frame})


@mcp.tool()
async def execute_menu_item(menu_path: str) -> dict[str, Any]:
    """Execute any Unity Editor menu item by its path.

    Examples:
        execute_menu_item('GameObject/3D Object/Cube')
        execute_menu_item('Assets/Refresh')
        execute_menu_item('File/Save Project')
    """
    return await _send("/execute_menu_item", {"menu_path": menu_path})


@mcp.tool()
async def set_play_mode(state: str) -> dict[str, Any]:
    """Control Editor play mode.

    state: 'play' | 'stop' | 'pause' | 'unpause'.
    """
    return await _send("/set_play_mode", {"state": state})


@mcp.tool()
async def get_play_mode() -> dict[str, Any]:
    """Return play/pause/compile/asset-update flags of the Editor."""
    return await _send("/get_play_mode")


@mcp.tool()
async def wait_for_compile(
    timeout: float = 60.0,
    poll_interval: float = 0.5,
    initial_wait: float = 0.5,
) -> dict[str, Any]:
    """Wait until Unity finishes compiling and reimporting assets.

    Polls `get_play_mode` and returns when both `is_compiling` and `is_updating`
    are false. Treats transport errors as 'bridge is reloading, keep trying' so
    it survives the assembly reload that follows `Assets/Refresh`.

    Typical use: after writing a .cs file and calling
    `execute_menu_item('Assets/Refresh')`, call this before `add_component`.

    Returns {"ok": true, "elapsed": <seconds>} on success,
    or {"error": "...", "last_response": {...}} on timeout.
    """
    loop = asyncio.get_event_loop()
    started = loop.time()
    await asyncio.sleep(max(0.0, initial_wait))

    last: dict[str, Any] = {}
    while True:
        last = await _send("/get_play_mode")
        if "error" not in last:
            if not last.get("is_compiling") and not last.get("is_updating"):
                return {"ok": True, "elapsed": round(loop.time() - started, 2)}
        if loop.time() - started > timeout:
            return {"error": f"Timed out after {timeout}s waiting for compile to finish", "last_response": last}
        await asyncio.sleep(poll_interval)


@mcp.tool()
async def get_console_logs(limit: int = 100, severity: str | None = None) -> dict[str, Any]:
    """Read the most recent Unity console logs captured since the bridge started.

    severity (optional): 'Log' | 'Warning' | 'Error' | 'Exception' | 'Assert'.
    The buffer keeps the last 1000 entries in-memory.
    """
    payload: dict[str, Any] = {"limit": limit}
    if severity is not None:
        payload["severity"] = severity
    return await _send("/get_console_logs", payload)


@mcp.tool()
async def clear_console_logs() -> dict[str, Any]:
    """Clear the bridge's in-memory log buffer (does not affect Unity's own console)."""
    return await _send("/clear_console_logs")


@mcp.tool()
async def import_asset(src_path: str, dst_path: str, overwrite: bool = False) -> dict[str, Any]:
    """Copy a file into the Unity project's Assets/ folder and import it.

    src_path: absolute filesystem path of the source file (e.g. '/tmp/Foo.fbx').
    dst_path: project-relative destination starting with 'Assets/'
              (e.g. 'Assets/Models/Foo.fbx'). Parent directories are created.
    overwrite: replace if the destination already exists.

    Triggers AssetDatabase.ImportAsset with ForceUpdate so the asset is
    immediately usable (e.g. with `instantiate_prefab`).
    """
    return await _send("/import_asset", {
        "src_path": src_path, "dst_path": dst_path, "overwrite": overwrite,
    })


if __name__ == "__main__":
    mcp.run()
