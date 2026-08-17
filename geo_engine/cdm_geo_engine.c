/*
 * CDM Geo Engine  —  DayZ/Arma Geometry LOD generator  v1.1.0
 * Compiles to cdm_geo_engine.dll (Windows) / cdm_geo_engine.so (Linux/macOS)
 *
 * Algorithm:
 *   1. Edge-adjacency graph (shared triangle edges)
 *   2. Island BFS via edge connectivity (separate mesh shells)
 *   3. CLOSED island  → one tight OBB per island
 *   4. OPEN island    → normal-angle BFS clusters inside island
 *   5. Antiparallel cluster merge (front/back wall pairs)
 *   6. OBB per cluster — one-sided thickening (matches Python _thicken_verts)
 */

#include <math.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#  define CDM_API __declspec(dllexport)
#else
#  define CDM_API __attribute__((visibility("default")))
#endif

typedef struct { float x, y, z; } Vec3;

static Vec3 vec3_sub(Vec3 a, Vec3 b) { return (Vec3){a.x-b.x, a.y-b.y, a.z-b.z}; }
static Vec3 vec3_add(Vec3 a, Vec3 b) { return (Vec3){a.x+b.x, a.y+b.y, a.z+b.z}; }
static Vec3 vec3_scale(Vec3 a, float s) { return (Vec3){a.x*s, a.y*s, a.z*s}; }
static float vec3_dot(Vec3 a, Vec3 b) { return a.x*b.x + a.y*b.y + a.z*b.z; }
static Vec3 vec3_cross(Vec3 a, Vec3 b) {
    return (Vec3){a.y*b.z - a.z*b.y, a.z*b.x - a.x*b.z, a.x*b.y - a.y*b.x};
}
static float vec3_len(Vec3 a) { return sqrtf(a.x*a.x + a.y*a.y + a.z*a.z); }
static Vec3 vec3_norm(Vec3 a) {
    float l = vec3_len(a);
    if (l < 1e-8f) return (Vec3){0,0,1};
    return vec3_scale(a, 1.0f/l);
}

static Vec3 face_normal(const float *verts, int i0, int i1, int i2) {
    Vec3 v0 = {verts[i0*3], verts[i0*3+1], verts[i0*3+2]};
    Vec3 v1 = {verts[i1*3], verts[i1*3+1], verts[i1*3+2]};
    Vec3 v2 = {verts[i2*3], verts[i2*3+1], verts[i2*3+2]};
    Vec3 e1 = vec3_sub(v1, v0);
    Vec3 e2 = vec3_sub(v2, v0);
    return vec3_norm(vec3_cross(e1, e2));
}

static float face_area(const float *verts, int i0, int i1, int i2) {
    Vec3 v0 = {verts[i0*3], verts[i0*3+1], verts[i0*3+2]};
    Vec3 v1 = {verts[i1*3], verts[i1*3+1], verts[i1*3+2]};
    Vec3 v2 = {verts[i2*3], verts[i2*3+1], verts[i2*3+2]};
    Vec3 e1 = vec3_sub(v1, v0);
    Vec3 e2 = vec3_sub(v2, v0);
    return vec3_len(vec3_cross(e1, e2)) * 0.5f;
}

typedef struct { int a, b; } Edge;

static Edge make_edge(int a, int b) {
    return (a < b) ? (Edge){a, b} : (Edge){b, a};
}

static int edge_eq(Edge e1, Edge e2) {
    return e1.a == e2.a && e1.b == e2.b;
}

typedef struct {
    int *data;
    int  head, tail, cap;
} IntQueue;

static IntQueue queue_new(int cap) {
    IntQueue q;
    q.data = (int*)malloc(cap * sizeof(int));
    q.head = q.tail = 0;
    q.cap  = cap;
    return q;
}
static void queue_push(IntQueue *q, int v) { q->data[q->tail++ % q->cap] = v; }
static int  queue_pop (IntQueue *q)        { return q->data[q->head++ % q->cap]; }
static int  queue_empty(IntQueue *q)       { return q->head == q->tail; }
static void queue_free(IntQueue *q)        { free(q->data); }

static int float_cmp(const void *a, const void *b) {
    float fa = *(const float*)a;
    float fb = *(const float*)b;
    if (fa < fb) return -1;
    if (fa > fb) return 1;
    return 0;
}

static void build_tangent_frame(Vec3 N, Vec3 *U, Vec3 *V) {
    Vec3 up = {0, 0, 1};
    if (fabsf(vec3_dot(N, up)) > 0.99f) up = (Vec3){1, 0, 0};
    *U = vec3_norm(vec3_sub(up, vec3_scale(N, vec3_dot(up, N))));
    *V = vec3_norm(vec3_cross(N, *U));
}

/* One-sided OBB — matches Python geo/clustering.py _thicken_verts */
static void compute_obb_open(const float *cluster_verts, int nv,
                           Vec3 N, float min_thickness,
                           float *out_corners /* 24 floats */) {
    Vec3 U, V;
    build_tangent_frame(N, &U, &V);

    float *n_projs = (float*)malloc(nv * sizeof(float));
    float *u_projs = (float*)malloc(nv * sizeof(float));
    float *v_projs = (float*)malloc(nv * sizeof(float));
    if (!n_projs || !u_projs || !v_projs) {
        free(n_projs); free(u_projs); free(v_projs);
        memset(out_corners, 0, 24 * sizeof(float));
        return;
    }

    for (int i = 0; i < nv; i++) {
        Vec3 p = {cluster_verts[i*3], cluster_verts[i*3+1], cluster_verts[i*3+2]};
        n_projs[i] = vec3_dot(p, N);
        u_projs[i] = vec3_dot(p, U);
        v_projs[i] = vec3_dot(p, V);
    }

    qsort(n_projs, nv, sizeof(float), float_cmp);
    int q1i = nv / 4;
    int q3i = (3 * nv) / 4;
    if (q3i >= nv) q3i = nv - 1;
    float n_q1 = n_projs[q1i];
    float n_q3 = n_projs[q3i];
    float n_surface = (n_q1 + n_q3) * 0.5f;
    float n_actual  = n_q3 - n_q1;
    float n_depth   = n_actual > min_thickness ? n_actual : min_thickness;
    float n_hi      = n_surface + 0.05f;
    float n_lo      = n_surface - n_depth;

    float u_min = u_projs[0], u_max = u_projs[0];
    float v_min = v_projs[0], v_max = v_projs[0];
    for (int i = 1; i < nv; i++) {
        if (u_projs[i] < u_min) u_min = u_projs[i];
        if (u_projs[i] > u_max) u_max = u_projs[i];
        if (v_projs[i] < v_min) v_min = v_projs[i];
        if (v_projs[i] > v_max) v_max = v_projs[i];
    }

    float min_h = min_thickness * 0.5f;
    if (u_max - u_min < min_thickness) {
        float uc = (u_min + u_max) * 0.5f;
        u_min = uc - min_h; u_max = uc + min_h;
    }
    if (v_max - v_min < min_thickness) {
        float vc = (v_min + v_max) * 0.5f;
        v_min = vc - min_h; v_max = vc + min_h;
    }

    int idx = 0;
    float ns[2] = {n_lo, n_hi};
    float us[2] = {u_min, u_max};
    float vs[2] = {v_min, v_max};
    for (int sn = 0; sn < 2; sn++)
    for (int su = 0; su < 2; su++)
    for (int sv = 0; sv < 2; sv++) {
        Vec3 c = vec3_add(vec3_add(vec3_scale(N, ns[sn]),
                               vec3_scale(U, us[su])),
                               vec3_scale(V, vs[sv]));
        out_corners[idx++] = c.x;
        out_corners[idx++] = c.y;
        out_corners[idx++] = c.z;
    }

    free(n_projs); free(u_projs); free(v_projs);
}

/* Tight OBB for closed (watertight) solids */
static void compute_obb_closed(const float *cluster_verts, int nv,
                             Vec3 N, float *out_corners) {
    Vec3 U, V;
    build_tangent_frame(N, &U, &V);

    float n_min=1e30f,n_max=-1e30f;
    float u_min=1e30f,u_max=-1e30f;
    float v_min=1e30f,v_max=-1e30f;
    for (int i = 0; i < nv; i++) {
        Vec3 p = {cluster_verts[i*3], cluster_verts[i*3+1], cluster_verts[i*3+2]};
        float pn=vec3_dot(p,N), pu=vec3_dot(p,U), pv=vec3_dot(p,V);
        if (pn<n_min) n_min=pn; if (pn>n_max) n_max=pn;
        if (pu<u_min) u_min=pu; if (pu>u_max) u_max=pu;
        if (pv<v_min) v_min=pv; if (pv>v_max) v_max=pv;
    }

    float min_pad = 0.001f;
    if (n_max - n_min < min_pad) {
        float cn = (n_min + n_max) * 0.5f;
        n_min = cn - min_pad * 0.5f; n_max = cn + min_pad * 0.5f;
    }
    if (u_max - u_min < min_pad) {
        float cu = (u_min + u_max) * 0.5f;
        u_min = cu - min_pad * 0.5f; u_max = cu + min_pad * 0.5f;
    }
    if (v_max - v_min < min_pad) {
        float cv = (v_min + v_max) * 0.5f;
        v_min = cv - min_pad * 0.5f; v_max = cv + min_pad * 0.5f;
    }

    int idx = 0;
    float ns[2] = {n_min, n_max};
    float us[2] = {u_min, u_max};
    float vs[2] = {v_min, v_max};
    for (int sn = 0; sn < 2; sn++)
    for (int su = 0; su < 2; su++)
    for (int sv = 0; sv < 2; sv++) {
        Vec3 c = vec3_add(vec3_add(vec3_scale(N, ns[sn]),
                               vec3_scale(U, us[su])),
                               vec3_scale(V, vs[sv]));
        out_corners[idx++] = c.x;
        out_corners[idx++] = c.y;
        out_corners[idx++] = c.z;
    }
}

typedef struct {
    int   *face_ids;
    int    n_faces;
    float  area;
    Vec3   avg_n;
    int   *vert_ids;
    int    n_verts;
} Cluster;

static void cluster_free(Cluster *c) {
    free(c->face_ids);
    free(c->vert_ids);
    memset(c, 0, sizeof(*c));
}

static Vec3 cluster_centroid(const float *verts, const int *vids, int nv) {
    Vec3 c = {0, 0, 0};
    if (nv <= 0) return c;
    for (int i = 0; i < nv; i++) {
        c.x += verts[vids[i]*3];
        c.y += verts[vids[i]*3+1];
        c.z += verts[vids[i]*3+2];
    }
    return vec3_scale(c, 1.0f / (float)nv);
}

static float cluster_span(const float *verts, const int *vids, int nv, Vec3 ctr) {
    float max_d = 0.0f;
    for (int i = 0; i < nv; i++) {
        Vec3 p = {verts[vids[i]*3], verts[vids[i]*3+1], verts[vids[i]*3+2]};
        Vec3 d = vec3_sub(p, ctr);
        float l = vec3_len(d);
        if (l > max_d) max_d = l;
    }
    return max_d;
}

static int merge_antiparallel_clusters(const float *verts,
                                     Cluster *clusters, int n_clusters) {
    if (n_clusters <= 1) return n_clusters;

    int *used = (int*)calloc(n_clusters, sizeof(int));
    Cluster *result = (Cluster*)malloc(n_clusters * sizeof(Cluster));
    if (!used || !result) {
        free(used); free(result);
        return n_clusters;
    }
    int n_result = 0;

    for (int i = 0; i < n_clusters; i++) {
        if (used[i]) continue;

        Vec3 c_i = cluster_centroid(verts, clusters[i].vert_ids, clusters[i].n_verts);
        float s_i = cluster_span(verts, clusters[i].vert_ids, clusters[i].n_verts, c_i);

        int best_j = -1;
        float best_dist = 1e30f;

        for (int j = i + 1; j < n_clusters; j++) {
            if (used[j]) continue;
            if (vec3_dot(clusters[i].avg_n, clusters[j].avg_n) > -0.8f) continue;

            Vec3 c_j = cluster_centroid(verts, clusters[j].vert_ids, clusters[j].n_verts);
            float s_j = cluster_span(verts, clusters[j].vert_ids, clusters[j].n_verts, c_j);
            Vec3 d = vec3_sub(c_i, c_j);
            float dist = vec3_len(d);
            float prox = 2.0f * (s_i > s_j ? s_i : s_j);
            if (prox < 0.01f) prox = 0.01f;
            if (dist < prox && dist < best_dist) {
                best_dist = dist;
                best_j = j;
            }
        }

        if (best_j >= 0) {
            Cluster *a = &clusters[i];
            Cluster *b = &clusters[best_j];
            Cluster m;
            memset(&m, 0, sizeof(m));
            m.n_faces = a->n_faces + b->n_faces;
            m.face_ids = (int*)malloc(m.n_faces * sizeof(int));
            memcpy(m.face_ids, a->face_ids, a->n_faces * sizeof(int));
            memcpy(m.face_ids + a->n_faces, b->face_ids, b->n_faces * sizeof(int));
            m.area = a->area + b->area;
            Vec3 diff = vec3_sub(a->avg_n, b->avg_n);
            m.avg_n = vec3_len(diff) > 1e-6f ? vec3_norm(diff) : a->avg_n;

            int max_v = a->n_verts + b->n_verts;
            m.vert_ids = (int*)malloc(max_v * sizeof(int));
            int nv = 0;
            for (int k = 0; k < a->n_verts; k++)
                m.vert_ids[nv++] = a->vert_ids[k];
            for (int k = 0; k < b->n_verts; k++) {
                int vi = b->vert_ids[k];
                int dup = 0;
                for (int t = 0; t < nv; t++)
                    if (m.vert_ids[t] == vi) { dup = 1; break; }
                if (!dup) m.vert_ids[nv++] = vi;
            }
            m.n_verts = nv;
            result[n_result++] = m;
            cluster_free(a);
            cluster_free(b);
            used[i] = used[best_j] = 1;
        } else {
            result[n_result++] = clusters[i];
            memset(&clusters[i], 0, sizeof(clusters[i]));
            used[i] = 1;
        }
    }

    for (int i = 0; i < n_clusters; i++)
        cluster_free(&clusters[i]);
    memcpy(clusters, result, n_result * sizeof(Cluster));
    free(used);
    free(result);
    return n_result;
}

CDM_API int cdm_generate_geo_boxes(
    const float *verts,   int n_verts,
    const int   *tris,    int n_tris,
    float  angle_thresh_deg,
    float  min_area,
    float  min_thickness,
    float *out_boxes,
    int    max_boxes)
{
    if (!verts || !tris || !out_boxes || n_verts <= 0 || n_tris <= 0)
        return 0;

    float cos_thresh = cosf(angle_thresh_deg * 3.14159265f / 180.0f);

    Vec3  *fnorm = (Vec3 *)malloc(n_tris * sizeof(Vec3));
    float *farea = (float*)malloc(n_tris * sizeof(float));
    if (!fnorm || !farea) {
        free(fnorm); free(farea);
        return -1;
    }
    for (int f = 0; f < n_tris; f++) {
        int i0 = tris[f*3+0], i1 = tris[f*3+1], i2 = tris[f*3+2];
        fnorm[f] = face_normal(verts, i0, i1, i2);
        farea[f] = face_area  (verts, i0, i1, i2);
    }

    /* Edge adjacency */
    int    n_edges   = n_tris * 3;
    Edge  *all_edges = (Edge*)malloc(n_edges * sizeof(Edge));
    int   *edge_face = (int *)malloc(n_edges * sizeof(int));
    int   *adj       = (int *)malloc(n_tris * 3 * sizeof(int));
    int   *adj_count = (int *)calloc(n_tris, sizeof(int));
    if (!all_edges || !edge_face || !adj || !adj_count) {
        free(fnorm); free(farea);
        free(all_edges); free(edge_face); free(adj); free(adj_count);
        return -1;
    }
    for (int i = 0; i < n_tris * 3; i++) adj[i] = -1;
    for (int f = 0; f < n_tris; f++) {
        for (int e = 0; e < 3; e++) {
            int a = tris[f*3 + e], b = tris[f*3 + (e+1)%3];
            all_edges[f*3+e] = make_edge(a, b);
            edge_face [f*3+e] = f;
        }
    }
    for (int i = 0; i < n_edges; i++) {
        for (int j = i+1; j < n_edges; j++) {
            if (edge_eq(all_edges[i], all_edges[j])) {
                int fi = edge_face[i], fj = edge_face[j];
                if (fi != fj) {
                    if (adj_count[fi] < 3) adj[fi*3 + adj_count[fi]++] = fj;
                    if (adj_count[fj] < 3) adj[fj*3 + adj_count[fj]++] = fi;
                }
            }
        }
    }
    free(all_edges); free(edge_face);

    /* Island BFS via edge adjacency */
    int *island_id = (int*)malloc(n_tris * sizeof(int));
    if (!island_id) {
        free(fnorm); free(farea); free(adj); free(adj_count);
        return -1;
    }
    for (int i = 0; i < n_tris; i++) island_id[i] = -1;
    int n_islands = 0;
    {
        IntQueue iq = queue_new(n_tris + 4);
        for (int seed = 0; seed < n_tris; seed++) {
            if (island_id[seed] >= 0) continue;
            int iid = n_islands++;
            island_id[seed] = iid;
            queue_push(&iq, seed);
            while (!queue_empty(&iq)) {
                int f = queue_pop(&iq);
                for (int a = 0; a < adj_count[f]; a++) {
                    int nb = adj[f*3 + a];
                    if (island_id[nb] < 0) {
                        island_id[nb] = iid;
                        queue_push(&iq, nb);
                    }
                }
            }
        }
        queue_free(&iq);
    }

    int   *island_closed   = (int  *)calloc(n_islands, sizeof(int));
    float *island_area_tot = (float*)calloc(n_islands, sizeof(float));
    if (!island_closed || !island_area_tot) {
        free(fnorm); free(farea); free(adj); free(adj_count);
        free(island_id); free(island_closed); free(island_area_tot);
        return -1;
    }
    for (int i = 0; i < n_islands; i++) island_closed[i] = 1;
    for (int f = 0; f < n_tris; f++) {
        int iid = island_id[f];
        island_area_tot[iid] += farea[f];
        for (int e = 0; e < 3; e++) {
            int nb = adj[f*3+e];
            if (nb < 0 || island_id[nb] != iid)
                island_closed[iid] = 0;
        }
    }

    int *face_visited = (int*)calloc(n_tris, sizeof(int));
    int *vert_seen    = (int*)calloc(n_verts, sizeof(int));
    if (!face_visited || !vert_seen) {
        free(fnorm); free(farea); free(adj); free(adj_count);
        free(island_id); free(island_closed); free(island_area_tot);
        free(face_visited); free(vert_seen);
        return -1;
    }

    int n_boxes = 0;
    const int MAX_CLUSTERS = 256;
    Cluster *clusters = (Cluster*)malloc(MAX_CLUSTERS * sizeof(Cluster));

    for (int iid = 0; iid < n_islands && n_boxes < max_boxes; iid++) {
        if (island_area_tot[iid] < min_area) continue;

        int n_cl = 0;

        if (island_closed[iid]) {
            /* One cluster = all faces in closed island */
            if (n_cl >= MAX_CLUSTERS) continue;
            Cluster *cl = &clusters[n_cl++];
            memset(cl, 0, sizeof(*cl));
            cl->n_faces = 0;
            for (int f = 0; f < n_tris; f++)
                if (island_id[f] == iid) cl->n_faces++;
            cl->face_ids = (int*)malloc(cl->n_faces * sizeof(int));
            int fi = 0;
            cl->area = 0.0f;
            cl->avg_n = (Vec3){0,0,0};
            for (int f = 0; f < n_tris; f++) {
                if (island_id[f] != iid) continue;
                cl->face_ids[fi++] = f;
                cl->area += farea[f];
                cl->avg_n.x += fnorm[f].x * farea[f];
                cl->avg_n.y += fnorm[f].y * farea[f];
                cl->avg_n.z += fnorm[f].z * farea[f];
            }
            cl->avg_n = vec3_norm(cl->avg_n);
            memset(vert_seen, 0, n_verts * sizeof(int));
            cl->n_verts = 0;
            for (int f = 0; f < n_tris; f++) {
                if (island_id[f] != iid) continue;
                for (int k = 0; k < 3; k++) {
                    int vi = tris[f*3+k];
                    if (!vert_seen[vi]) { vert_seen[vi] = 1; cl->n_verts++; }
                }
            }
            cl->vert_ids = (int*)malloc(cl->n_verts * sizeof(int));
            memset(vert_seen, 0, n_verts * sizeof(int));
            int vi_out = 0;
            for (int f = 0; f < n_tris; f++) {
                if (island_id[f] != iid) continue;
                for (int k = 0; k < 3; k++) {
                    int vi = tris[f*3+k];
                    if (!vert_seen[vi]) {
                        vert_seen[vi] = 1;
                        cl->vert_ids[vi_out++] = vi;
                    }
                }
            }
        } else {
            /* Normal-angle BFS within open island */
            for (int seed = 0; seed < n_tris && n_cl < MAX_CLUSTERS; seed++) {
                if (island_id[seed] != iid || face_visited[seed]) continue;

                Cluster cl;
                memset(&cl, 0, sizeof(cl));
                cl.area = 0.0f;
                cl.avg_n = (Vec3){0,0,0};

                int cap = 64;
                cl.face_ids = (int*)malloc(cap * sizeof(int));
                cl.n_faces = 0;

                IntQueue q = queue_new(n_tris + 4);
                face_visited[seed] = 1;
                queue_push(&q, seed);

                while (!queue_empty(&q)) {
                    int f = queue_pop(&q);
                    if (cl.n_faces >= cap) {
                        cap *= 2;
                        cl.face_ids = (int*)realloc(cl.face_ids, cap * sizeof(int));
                    }
                    cl.face_ids[cl.n_faces++] = f;
                    cl.area += farea[f];
                    cl.avg_n.x += fnorm[f].x * farea[f];
                    cl.avg_n.y += fnorm[f].y * farea[f];
                    cl.avg_n.z += fnorm[f].z * farea[f];

                    Vec3 fn = fnorm[f];
                    for (int a = 0; a < adj_count[f]; a++) {
                        int nb = adj[f*3 + a];
                        if (island_id[nb] != iid || face_visited[nb]) continue;
                        if (vec3_dot(fn, fnorm[nb]) >= cos_thresh) {
                            face_visited[nb] = 1;
                            queue_push(&q, nb);
                        }
                    }
                }
                queue_free(&q);

                if (cl.area < min_area || cl.n_faces == 0) {
                    free(cl.face_ids);
                    continue;
                }
                cl.avg_n = vec3_norm(cl.avg_n);

                memset(vert_seen, 0, n_verts * sizeof(int));
                cl.n_verts = 0;
                for (int fi = 0; fi < cl.n_faces; fi++) {
                    int f = cl.face_ids[fi];
                    for (int k = 0; k < 3; k++) {
                        int vi = tris[f*3+k];
                        if (!vert_seen[vi]) { vert_seen[vi] = 1; cl.n_verts++; }
                    }
                }
                if (cl.n_verts < 2) { free(cl.face_ids); continue; }

                cl.vert_ids = (int*)malloc(cl.n_verts * sizeof(int));
                memset(vert_seen, 0, n_verts * sizeof(int));
                int vi_out = 0;
                for (int fi = 0; fi < cl.n_faces; fi++) {
                    int f = cl.face_ids[fi];
                    for (int k = 0; k < 3; k++) {
                        int vi = tris[f*3+k];
                        if (!vert_seen[vi]) {
                            vert_seen[vi] = 1;
                            cl.vert_ids[vi_out++] = vi;
                        }
                    }
                }
                clusters[n_cl++] = cl;
            }
            n_cl = merge_antiparallel_clusters(verts, clusters, n_cl);
        }

        for (int c = 0; c < n_cl && n_boxes < max_boxes; c++) {
            Cluster *cl = &clusters[c];
            if (cl->n_verts < 2) continue;

            float *cverts = (float*)malloc(cl->n_verts * 3 * sizeof(float));
            if (!cverts) break;
            for (int i = 0; i < cl->n_verts; i++) {
                int vi = cl->vert_ids[i];
                cverts[i*3+0] = verts[vi*3+0];
                cverts[i*3+1] = verts[vi*3+1];
                cverts[i*3+2] = verts[vi*3+2];
            }

            if (island_closed[iid]) {
                if (cl->n_verts >= 4)
                    compute_obb_closed(cverts, cl->n_verts, cl->avg_n,
                                     out_boxes + n_boxes * 24);
                else {
                    free(cverts);
                    continue;
                }
            } else {
                compute_obb_open(cverts, cl->n_verts, cl->avg_n, min_thickness,
                               out_boxes + n_boxes * 24);
            }
            free(cverts);
            n_boxes++;
        }

        for (int c = 0; c < n_cl; c++)
            cluster_free(&clusters[c]);
    }

    free(fnorm); free(farea);
    free(adj); free(adj_count);
    free(island_id); free(island_closed); free(island_area_tot);
    free(face_visited); free(vert_seen);
    free(clusters);

    return n_boxes;
}

CDM_API const char* cdm_version(void) {
    return "CDM Geo Engine 1.2.1";
}

/* ── Shell engine: copy mesh → one rectangular box per edge-island ───────── */

static void compute_rect_shell(const float *iv, int nv, Vec3 N, float pad,
                               float *out_corners /* 24 floats */) {
    Vec3 U, V;
    build_tangent_frame(N, &U, &V);

    float n_min=1e30f,n_max=-1e30f;
    float u_min=1e30f,u_max=-1e30f;
    float v_min=1e30f,v_max=-1e30f;
    for (int i = 0; i < nv; i++) {
        Vec3 p = {iv[i*3], iv[i*3+1], iv[i*3+2]};
        float pn=vec3_dot(p,N), pu=vec3_dot(p,U), pv=vec3_dot(p,V);
        if (pn<n_min) n_min=pn; if (pn>n_max) n_max=pn;
        if (pu<u_min) u_min=pu; if (pu>u_max) u_max=pu;
        if (pv<v_min) v_min=pv; if (pv>v_max) v_max=pv;
    }

    n_min -= pad; n_max += pad;
    u_min -= pad; u_max += pad;
    v_min -= pad; v_max += pad;

    float min_span = pad * 2.0f;
    if (n_max - n_min < min_span) {
        float c = (n_min + n_max) * 0.5f;
        n_min = c - min_span * 0.5f; n_max = c + min_span * 0.5f;
    }
    if (u_max - u_min < min_span) {
        float c = (u_min + u_max) * 0.5f;
        u_min = c - min_span * 0.5f; u_max = c + min_span * 0.5f;
    }
    if (v_max - v_min < min_span) {
        float c = (v_min + v_max) * 0.5f;
        v_min = c - min_span * 0.5f; v_max = c + min_span * 0.5f;
    }

    int idx = 0;
    float ns[2] = {n_min, n_max};
    float us[2] = {u_min, u_max};
    float vs[2] = {v_min, v_max};
    for (int sn = 0; sn < 2; sn++)
    for (int su = 0; su < 2; su++)
    for (int sv = 0; sv < 2; sv++) {
        Vec3 c = vec3_add(vec3_add(vec3_scale(N, ns[sn]),
                               vec3_scale(U, us[su])),
                               vec3_scale(V, vs[sv]));
        out_corners[idx++] = c.x;
        out_corners[idx++] = c.y;
        out_corners[idx++] = c.z;
    }
}

static float island_footprint_area(const float *iv, int nv) {
    float x_min=1e30f,x_max=-1e30f,y_min=1e30f,y_max=-1e30f,z_min=1e30f,z_max=-1e30f;
    for (int i = 0; i < nv; i++) {
        float x=iv[i*3], y=iv[i*3+1], z=iv[i*3+2];
        if (x<x_min) x_min=x; if (x>x_max) x_max=x;
        if (y<y_min) y_min=y; if (y>y_max) y_max=y;
        if (z<z_min) z_min=z; if (z>z_max) z_max=z;
    }
    float ex[3] = {x_max-x_min, y_max-y_min, z_max-z_min};
    /* simple sort 3 elements descending */
    for (int i = 0; i < 2; i++)
        for (int j = i+1; j < 3; j++)
            if (ex[j] > ex[i]) { float t=ex[i]; ex[i]=ex[j]; ex[j]=t; }
    return ex[0] * ex[1];
}

/*
 * cdm_generate_shell_boxes — DayZ Schutzhülle (closed building mesh OK)
 *
 * Face-angle clusters on the full connected mesh, antiparallel merge,
 * one rectangular shell box per wall/roof/floor slab + shell_pad.
 */
CDM_API int cdm_generate_shell_boxes(
    const float *verts,   int n_verts,
    const int   *tris,    int n_tris,
    float  angle_thresh_deg,
    float  min_area,
    float  shell_pad,
    float *out_boxes,
    int    max_boxes)
{
    if (!verts || !tris || !out_boxes || n_verts <= 0 || n_tris <= 0)
        return 0;
    if (shell_pad < 1e-6f) shell_pad = 0.001f;

    float cos_thresh = cosf(angle_thresh_deg * 3.14159265f / 180.0f);

    Vec3  *fnorm = (Vec3 *)malloc(n_tris * sizeof(Vec3));
    float *farea = (float*)malloc(n_tris * sizeof(float));
    if (!fnorm || !farea) {
        free(fnorm); free(farea);
        return -1;
    }
    for (int f = 0; f < n_tris; f++) {
        int i0 = tris[f*3+0], i1 = tris[f*3+1], i2 = tris[f*3+2];
        fnorm[f] = face_normal(verts, i0, i1, i2);
        farea[f] = face_area  (verts, i0, i1, i2);
    }

    int    n_edges   = n_tris * 3;
    Edge  *all_edges = (Edge*)malloc(n_edges * sizeof(Edge));
    int   *edge_face = (int *)malloc(n_edges * sizeof(int));
    int   *adj       = (int *)malloc(n_tris * 3 * sizeof(int));
    int   *adj_count = (int *)calloc(n_tris, sizeof(int));
    if (!all_edges || !edge_face || !adj || !adj_count) {
        free(fnorm); free(farea);
        free(all_edges); free(edge_face); free(adj); free(adj_count);
        return -1;
    }
    for (int i = 0; i < n_tris * 3; i++) adj[i] = -1;
    for (int f = 0; f < n_tris; f++) {
        for (int e = 0; e < 3; e++) {
            int a = tris[f*3+e], b = tris[f*3+(e+1)%3];
            all_edges[f*3+e] = make_edge(a, b);
            edge_face[f*3+e] = f;
        }
    }
    for (int i = 0; i < n_edges; i++) {
        for (int j = i+1; j < n_edges; j++) {
            if (edge_eq(all_edges[i], all_edges[j])) {
                int fi = edge_face[i], fj = edge_face[j];
                if (fi != fj) {
                    if (adj_count[fi] < 3) adj[fi*3 + adj_count[fi]++] = fj;
                    if (adj_count[fj] < 3) adj[fj*3 + adj_count[fj]++] = fi;
                }
            }
        }
    }
    free(all_edges); free(edge_face);

    int *face_visited = (int*)calloc(n_tris, sizeof(int));
    int *vert_seen    = (int*)calloc(n_verts, sizeof(int));
    const int MAX_CLUSTERS = 256;
    Cluster *clusters = (Cluster*)malloc(MAX_CLUSTERS * sizeof(Cluster));
    if (!face_visited || !vert_seen || !clusters) {
        free(fnorm); free(farea); free(adj); free(adj_count);
        free(face_visited); free(vert_seen); free(clusters);
        return -1;
    }

    int n_cl = 0;
    for (int seed = 0; seed < n_tris && n_cl < MAX_CLUSTERS; seed++) {
        if (face_visited[seed]) continue;

        Cluster cl;
        memset(&cl, 0, sizeof(cl));
        cl.area = 0.0f;
        cl.avg_n = (Vec3){0,0,0};
        int cap = 64;
        cl.face_ids = (int*)malloc(cap * sizeof(int));
        cl.n_faces = 0;

        IntQueue q = queue_new(n_tris + 4);
        face_visited[seed] = 1;
        queue_push(&q, seed);

        while (!queue_empty(&q)) {
            int f = queue_pop(&q);
            if (cl.n_faces >= cap) {
                cap *= 2;
                cl.face_ids = (int*)realloc(cl.face_ids, cap * sizeof(int));
            }
            cl.face_ids[cl.n_faces++] = f;
            cl.area += farea[f];
            cl.avg_n.x += fnorm[f].x * farea[f];
            cl.avg_n.y += fnorm[f].y * farea[f];
            cl.avg_n.z += fnorm[f].z * farea[f];

            Vec3 fn = fnorm[f];
            for (int a = 0; a < adj_count[f]; a++) {
                int nb = adj[f*3 + a];
                if (face_visited[nb]) continue;
                if (vec3_dot(fn, fnorm[nb]) >= cos_thresh) {
                    face_visited[nb] = 1;
                    queue_push(&q, nb);
                }
            }
        }
        queue_free(&q);

        if (cl.area < min_area || cl.n_faces == 0) {
            free(cl.face_ids);
            continue;
        }
        cl.avg_n = vec3_norm(cl.avg_n);

        memset(vert_seen, 0, n_verts * sizeof(int));
        cl.n_verts = 0;
        for (int fi = 0; fi < cl.n_faces; fi++) {
            int f = cl.face_ids[fi];
            for (int k = 0; k < 3; k++) {
                int vi = tris[f*3+k];
                if (!vert_seen[vi]) { vert_seen[vi] = 1; cl.n_verts++; }
            }
        }
        if (cl.n_verts < 2) { free(cl.face_ids); continue; }

        cl.vert_ids = (int*)malloc(cl.n_verts * sizeof(int));
        memset(vert_seen, 0, n_verts * sizeof(int));
        int vi_out = 0;
        for (int fi = 0; fi < cl.n_faces; fi++) {
            int f = cl.face_ids[fi];
            for (int k = 0; k < 3; k++) {
                int vi = tris[f*3+k];
                if (!vert_seen[vi]) {
                    vert_seen[vi] = 1;
                    cl.vert_ids[vi_out++] = vi;
                }
            }
        }
        clusters[n_cl++] = cl;
    }

    n_cl = merge_antiparallel_clusters(verts, clusters, n_cl);

    int n_boxes = 0;
    for (int c = 0; c < n_cl && n_boxes < max_boxes; c++) {
        Cluster *cl = &clusters[c];
        if (cl->n_verts < 2) continue;

        float *iv = (float*)malloc(cl->n_verts * 3 * sizeof(float));
        if (!iv) break;
        for (int i = 0; i < cl->n_verts; i++) {
            int vi = cl->vert_ids[i];
            iv[i*3+0] = verts[vi*3+0];
            iv[i*3+1] = verts[vi*3+1];
            iv[i*3+2] = verts[vi*3+2];
        }
        compute_rect_shell(iv, cl->n_verts, cl->avg_n, shell_pad,
                           out_boxes + n_boxes * 24);
        free(iv);
        n_boxes++;
    }

    for (int c = 0; c < n_cl; c++)
        cluster_free(&clusters[c]);

    free(fnorm); free(farea);
    free(adj); free(adj_count);
    free(face_visited); free(vert_seen);
    free(clusters);
    return n_boxes;
}

}

}

}

}

}

}

}


}

}

}

}

}

}

}

}



























}

}





