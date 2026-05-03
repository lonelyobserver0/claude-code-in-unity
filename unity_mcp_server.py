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


if __name__ == "__main__":
    mcp.run()
