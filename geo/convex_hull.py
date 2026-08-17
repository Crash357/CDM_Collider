"""Convex hull helpers using Blender's bmesh.ops.convex_hull."""
import bmesh
import bpy
import mathutils


def convex_hull_bmesh(points):
    """
    Build a convex hull bmesh from world-space points.

    Uses Blender's public bmesh API: add verts, convex_hull, drop unused/interior.
    Caller owns the returned bmesh and must call bm.free().
    """
    bm = bmesh.new()
    for co in points:
        bm.verts.new(co)
    ch = bmesh.ops.convex_hull(bm, input=bm.verts)
    to_delete = []
    for key in ("geom_unused", "geom_interior"):
        to_delete.extend(ch.get(key) or [])
    if to_delete:
        unique_delete = list({id(g): g for g in to_delete}.values())
        bmesh.ops.delete(bm, geom=unique_delete, context='VERTS')
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    from .helpers import ensure_outward_normals
    ensure_outward_normals(bm)
    return bm


def convex_hull_verts(points):
    """Return hull surface vertex positions; empty if fewer than 4 input points."""
    if len(points) < 4:
        return []
    bm = convex_hull_bmesh(points)
    verts = [mathutils.Vector(v.co) for v in bm.verts]
    bm.free()
    return verts


def convex_hull_mesh(points, name):
    """Create a validated Blender mesh from world-space points."""
    if len(points) < 4:
        return None
    bm = convex_hull_bmesh(points)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.validate()
    mesh.update()
    return mesh
