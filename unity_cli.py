#!/usr/bin/env python3
"""Direct CLI to the Unity MCP Bridge — no MCP required.

Use this from tools that can run shell commands but don't speak MCP (e.g. Aider
via `/run`, Makefiles, shell scripts). Each subcommand maps 1:1 to a bridge
endpoint and prints the JSON response on stdout. Exit code is 0 on `ok`/`success`,
1 if the response contains an `error` field, 2 on transport errors.

Reads the same environment variables as `unity_mcp_server.py`:

    UNITY_MCP_URL      base URL of the Unity bridge   (default: http://127.0.0.1:8080)
    UNITY_MCP_TOKEN    shared auth token              (required)
    UNITY_MCP_TIMEOUT  per-request timeout in seconds (default: 10.0)
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any

import httpx

UNITY_URL = os.environ.get("UNITY_MCP_URL", "http://127.0.0.1:8080").rstrip("/")
UNITY_TOKEN = os.environ.get("UNITY_MCP_TOKEN", "")
UNITY_TIMEOUT = float(os.environ.get("UNITY_MCP_TIMEOUT", "10.0"))


def _send(path: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
    if not UNITY_TOKEN:
        return {"error": "UNITY_MCP_TOKEN is not set. Copy the token from Unity > Tools > MCP Bridge > Control Panel."}

    headers = {"X-MCP-Token": UNITY_TOKEN, "Content-Type": "application/json"}
    try:
        response = httpx.post(
            f"{UNITY_URL}{path}",
            json=payload or {},
            headers=headers,
            timeout=UNITY_TIMEOUT,
        )
    except httpx.TimeoutException:
        return {"error": f"Unity bridge timed out after {UNITY_TIMEOUT}s on {path}"}
    except httpx.RequestError as e:
        return {"error": f"Connection failed: {e}"}

    if response.status_code == 401:
        return {"error": "Unauthorized: token mismatch between this CLI and Unity."}
    try:
        return response.json()
    except ValueError:
        return {"error": f"HTTP {response.status_code}: {response.text[:200]}"}


def _vec3(s: str) -> dict[str, float]:
    """Parse 'x,y,z' into {'x':..,'y':..,'z':..}."""
    parts = [p.strip() for p in s.split(",")]
    if len(parts) != 3:
        raise argparse.ArgumentTypeError(f"expected 'x,y,z', got {s!r}")
    try:
        x, y, z = (float(p) for p in parts)
    except ValueError as e:
        raise argparse.ArgumentTypeError(f"invalid float in {s!r}: {e}")
    return {"x": x, "y": y, "z": z}


def _json_arg(s: str) -> Any:
    try:
        return json.loads(s)
    except json.JSONDecodeError as e:
        raise argparse.ArgumentTypeError(f"invalid JSON: {e}")


def _emit(resp: dict[str, Any]) -> int:
    print(json.dumps(resp, indent=2, ensure_ascii=False))
    return 1 if "error" in resp else 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="unity_cli",
        description="Direct CLI for the Unity MCP Bridge. Each subcommand maps to a bridge endpoint.",
    )
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("ping", help="Health check.")

    s = sub.add_parser("create_object", help="Create a primitive (Cube/Sphere/Plane/Cylinder/Capsule/Quad/Empty).")
    s.add_argument("--type", default="Cube")
    s.add_argument("--name", default="NewObject")

    s = sub.add_parser("delete_object", help="Delete a GameObject by instance id.")
    s.add_argument("id", type=int)

    s = sub.add_parser("set_transform", help="Set position/rotation/scale on a GameObject.")
    s.add_argument("id", type=int)
    s.add_argument("--position", type=_vec3, help="x,y,z")
    s.add_argument("--rotation", type=_vec3, help="x,y,z (euler degrees)")
    s.add_argument("--scale", type=_vec3, help="x,y,z")

    s = sub.add_parser("get_object_info", help="Return info on a GameObject.")
    s.add_argument("id", type=int)

    s = sub.add_parser("set_component_property", help="Set a public field/property via reflection.")
    s.add_argument("id", type=int)
    s.add_argument("component")
    s.add_argument("property")
    g = s.add_mutually_exclusive_group(required=True)
    g.add_argument("--value", help="Scalar value (string/number/bool, parsed as JSON if possible).")
    g.add_argument("--value-json", type=_json_arg, help="Complex value as JSON, e.g. '{\"r\":1,\"g\":0,\"b\":0,\"a\":1}'.")

    s = sub.add_parser("instantiate_prefab", help="Instantiate a prefab from AssetDatabase.")
    s.add_argument("path")
    s.add_argument("--name")

    sub.add_parser("list_scene_objects", help="List active-scene root objects.")

    s = sub.add_parser("add_component", help="Add a component by type name.")
    s.add_argument("id", type=int)
    s.add_argument("component")

    s = sub.add_parser("remove_component", help="Remove a component by type name.")
    s.add_argument("id", type=int)
    s.add_argument("component")

    s = sub.add_parser("find_object", help="Find by name (recursive) or hierarchy path.")
    g = s.add_mutually_exclusive_group(required=True)
    g.add_argument("--name")
    g.add_argument("--path")

    s = sub.add_parser("get_children", help="Direct children of a GameObject.")
    s.add_argument("id", type=int)

    s = sub.add_parser("set_parent", help="Reparent a GameObject (omit --parent-id to detach to root).")
    s.add_argument("id", type=int)
    s.add_argument("--parent-id", type=int)
    s.add_argument("--reset-local", action="store_true", help="Reset local transform after reparenting.")

    s = sub.add_parser("set_active", help="Enable/disable a GameObject.")
    s.add_argument("id", type=int)
    s.add_argument("active", choices=["true", "false"])

    s = sub.add_parser("set_tag", help="Set tag (must already exist in TagManager).")
    s.add_argument("id", type=int)
    s.add_argument("tag")

    s = sub.add_parser("set_layer", help="Set layer (int 0-31 or layer name).")
    s.add_argument("id", type=int)
    s.add_argument("layer")

    s = sub.add_parser("save_scene", help="Save the active scene (optional save-as path).")
    s.add_argument("--path")

    s = sub.add_parser("open_scene", help="Open a scene from disk.")
    s.add_argument("path")
    s.add_argument("--mode", default="Single", choices=["Single", "Additive", "AdditiveWithoutLoading"])

    s = sub.add_parser("new_scene", help="Create a new scene.")
    s.add_argument("--setup", default="DefaultGameObjects", choices=["DefaultGameObjects", "EmptyScene"])

    sub.add_parser("get_scene_info", help="Active-scene info.")

    s = sub.add_parser("select_object", help="Select + ping a GameObject (and optionally frame it).")
    s.add_argument("id", type=int)
    s.add_argument("--frame", action="store_true")

    s = sub.add_parser("execute_menu_item", help="Run any Editor menu item by path.")
    s.add_argument("menu_path")

    s = sub.add_parser("set_play_mode", help="play | stop | pause | unpause")
    s.add_argument("state", choices=["play", "stop", "pause", "unpause"])

    sub.add_parser("get_play_mode", help="Return play/pause/compile/asset-update flags.")

    s = sub.add_parser("get_console_logs", help="Read captured Unity console logs.")
    s.add_argument("--limit", type=int, default=100)
    s.add_argument("--severity", choices=["Log", "Warning", "Error", "Exception", "Assert"])

    sub.add_parser("clear_console_logs", help="Clear the bridge's in-memory log buffer.")

    s = sub.add_parser("call", help="Escape hatch: POST raw JSON to an arbitrary bridge path.")
    s.add_argument("path", help="e.g. /create_object")
    s.add_argument("--json", dest="json_payload", type=_json_arg, default={}, help="JSON body, default '{}'.")

    return p


def dispatch(args: argparse.Namespace) -> dict[str, Any]:
    cmd = args.cmd

    if cmd == "ping":
        return _send("/ping")

    if cmd == "create_object":
        return _send("/create_object", {"type": args.type, "name": args.name})

    if cmd == "delete_object":
        return _send("/delete_object", {"id": args.id})

    if cmd == "set_transform":
        payload: dict[str, Any] = {"id": args.id}
        if args.position is not None: payload["position"] = args.position
        if args.rotation is not None: payload["rotation"] = args.rotation
        if args.scale is not None:    payload["scale"] = args.scale
        return _send("/set_transform", payload)

    if cmd == "get_object_info":
        return _send("/get_object_info", {"id": args.id})

    if cmd == "set_component_property":
        if args.value_json is not None:
            value: Any = args.value_json
        else:
            try:
                value = json.loads(args.value)
            except (json.JSONDecodeError, TypeError):
                value = args.value
        return _send("/set_component_property", {
            "id": args.id, "component": args.component,
            "property": args.property, "value": value,
        })

    if cmd == "instantiate_prefab":
        payload = {"path": args.path}
        if args.name is not None: payload["name"] = args.name
        return _send("/instantiate_prefab", payload)

    if cmd == "list_scene_objects":
        return _send("/list_scene_objects")

    if cmd == "add_component":
        return _send("/add_component", {"id": args.id, "component": args.component})

    if cmd == "remove_component":
        return _send("/remove_component", {"id": args.id, "component": args.component})

    if cmd == "find_object":
        payload = {}
        if args.name is not None: payload["name"] = args.name
        if args.path is not None: payload["path"] = args.path
        return _send("/find_object", payload)

    if cmd == "get_children":
        return _send("/get_children", {"id": args.id})

    if cmd == "set_parent":
        payload = {"id": args.id, "world_position_stays": not args.reset_local}
        if args.parent_id is not None: payload["parent_id"] = args.parent_id
        return _send("/set_parent", payload)

    if cmd == "set_active":
        return _send("/set_active", {"id": args.id, "active": args.active == "true"})

    if cmd == "set_tag":
        return _send("/set_tag", {"id": args.id, "tag": args.tag})

    if cmd == "set_layer":
        layer: Any = args.layer
        try: layer = int(args.layer)
        except ValueError: pass
        return _send("/set_layer", {"id": args.id, "layer": layer})

    if cmd == "save_scene":
        payload = {}
        if args.path is not None: payload["path"] = args.path
        return _send("/save_scene", payload)

    if cmd == "open_scene":
        return _send("/open_scene", {"path": args.path, "mode": args.mode})

    if cmd == "new_scene":
        return _send("/new_scene", {"setup": args.setup})

    if cmd == "get_scene_info":
        return _send("/get_scene_info")

    if cmd == "select_object":
        return _send("/select_object", {"id": args.id, "frame": args.frame})

    if cmd == "execute_menu_item":
        return _send("/execute_menu_item", {"menu_path": args.menu_path})

    if cmd == "set_play_mode":
        return _send("/set_play_mode", {"state": args.state})

    if cmd == "get_play_mode":
        return _send("/get_play_mode")

    if cmd == "get_console_logs":
        payload = {"limit": args.limit}
        if args.severity is not None: payload["severity"] = args.severity
        return _send("/get_console_logs", payload)

    if cmd == "clear_console_logs":
        return _send("/clear_console_logs")

    if cmd == "call":
        path = args.path if args.path.startswith("/") else "/" + args.path
        return _send(path, args.json_payload)

    return {"error": f"Unknown command: {cmd}"}


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return _emit(dispatch(args))


if __name__ == "__main__":
    sys.exit(main())
