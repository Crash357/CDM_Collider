"""CDM Collider — Geometry LOD builder."""
import bmesh
import bpy
import mathutils

from .constants import BOX_FACES
from .convex_hull import convex_hull_bmesh, convex_hull_verts
from .helpers import move_to_collection

class _GeoLODBuilder:
    """
    Accumulates box/hull components, then builds ONE Blender object
    containing all islands, each tagged with a ComponentXX vertex group.

    Two storage lists:
      self.boxes  — list of 8-corner tuples from _thicken_verts.
                    Written as a direct hexahedron (8 verts, 6 quads).
                    No convex-hull calculation — we already know the shape.
      self.hulls  — list of arbitrary point clouds (HULL method).
                    Written via bmesh.ops.convex_hull.
    """

    def __init__(self):
        self.boxes  = []   # list of [mathutils.Vector × 8]  (box corners, ordered)
        self.hulls  = []   # list of [mathutils.Vector, ...]  (arbitrary cloud)

    # ── Box path: 8 ordered corners, no convex-hull needed ────────────────
    def add_box(self, corners_8):
        """
        Store an OBB as a direct hexahedron.
        corners_8 must be the 8-element list from _thicken_verts:
          index = sn_bit*4 + su_bit*2 + sv_bit   (sn=0→n_lo, sn=1→n_hi, etc.)
        Returns True if valid.
        """
        if len(corners_8) != 8:
            return False
        self.boxes.append([mathutils.Vector(c) for c in corners_8])
        return True

    # ── Hull path: arbitrary point cloud ──────────────────────────────────
    def add_hull(self, world_verts):
        """Compute convex hull and store it. Returns True if successful."""
        hull_verts = convex_hull_verts(world_verts)
        if len(hull_verts) < 4:
            return False
        self.hulls.append(hull_verts)
        return True

    @property
    def component_count(self):
        return len(self.boxes) + len(self.hulls)

    def finalize(self, name="Geometry"):
        """
        Merge all components into one Blender object.
        Each component = disconnected mesh island + ComponentXX vertex group.
        Returns the created object, or None if empty.
        """
        if not self.boxes and not self.hulls:
            return None

        master_bm = bmesh.new()
        island_vert_lists = []   # (comp_name, [BMVert, ...])
        comp_idx = 1

        # ── 1. Direct boxes (8 verts, 6 quads — NO convex hull) ──────────
        # Corner order from _thicken_verts:
        #   for sn in (n_lo, n_hi):       bit 2 of index
        #     for su in (u_min, u_max):   bit 1 of index
        #       for sv in (v_min, v_max): bit 0 of index
        #
        # Faces: each face = 4 corners sharing one axis value
        for corners in self.boxes:
            comp_name = "Component{:02d}".format(comp_idx)
            verts = [master_bm.verts.new(c) for c in corners]
            for fi in BOX_FACES:
                try:
                    master_bm.faces.new([verts[i] for i in fi])
                except ValueError:
                    pass  # duplicate face — skip
            island_vert_lists.append((comp_name, verts))
            comp_idx += 1

        # ── 2. Convex hulls (organic shapes) ─────────────────────────────
        for hull_verts in self.hulls:
            comp_name = "Component{:02d}".format(comp_idx)
            hull_bm = convex_hull_bmesh(hull_verts)
            new_verts = []
            vert_map = {}
            for v in hull_bm.verts:
                nv = master_bm.verts.new(v.co)
                vert_map[v] = nv
                new_verts.append(nv)
            for face in hull_bm.faces:
                try:
                    master_bm.faces.new([vert_map[v] for v in face.verts])
                except ValueError:
                    pass
            hull_bm.free()
            island_vert_lists.append((comp_name, new_verts))
            comp_idx += 1

        master_bm.verts.ensure_lookup_table()
        island_vert_indices = [
            (comp_name, [v.index for v in vl])
            for comp_name, vl in island_vert_lists
        ]

        result_mesh = bpy.data.meshes.new(name)
        master_bm.to_mesh(result_mesh)
        master_bm.free()
        result_mesh.validate()
        result_mesh.update()

        obj = bpy.data.objects.new(name, result_mesh)

        for comp_name, vert_indices in island_vert_indices:
            if vert_indices:
                vg = obj.vertex_groups.new(name=comp_name)
                vg.add(vert_indices, 1.0, 'REPLACE')

        from .helpers import apply_geometry_lod_metadata
        apply_geometry_lod_metadata(obj)

        mat = bpy.data.materials.get("cdm_geo") or bpy.data.materials.new("cdm_geo")
        result_mesh.materials.append(mat)

        move_to_collection(obj, "Geometry")
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        from .helpers import apply_scene_geometry_mass
        apply_scene_geometry_mass(obj)
        return obj
