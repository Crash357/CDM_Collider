"""CDM Collider — optional C geo engine bridge."""
try:
    from ..geo_engine.cdm_engine import generate_geo_boxes as engine_generate
    from ..geo_engine.cdm_engine import engine_available as engine_ok
except Exception:
    def engine_generate(*a, **kw):
        return []
    def engine_ok():
        return False
