#version 430 core
#extension GL_ARB_bindless_texture : require

in vec2  vTex;
in vec3  vTint;
in float vFogFactor;
in vec3  vDir;         // model-space view direction (enhanced night sky)
out vec4 fragColor;

uniform float uParamA;
uniform float uParamB;

uniform uint uTextureIndexA;

// The per-draw block sky.vert declares. A uniform block must be declared
// identically in every stage of a program that names it — the same rule the
// SceneLighting block below has always followed — so the whole thing appears
// here even though the fragment stage reads only the last three scalars.
layout(std140, ACDREAM_UBO_SET binding = 4) uniform SkyParams {
    mat4  uModel;
    mat4  uSkyView;
    mat4  uSkyProjection;
    vec3  uAmbientColor;
    float uEmissive;
    vec3  uSunColor;
    float uDiffuseFactor;
    vec3  uSunDir;
    float uTransparency;
    vec2  uUvScroll;
    float uApplyFog;
    float uSurfOpacity;
};

struct Light {
    vec4 posAndKind;
    vec4 dirAndRange;
    vec4 colorAndIntensity;
    vec4 coneAngleEtc;
};
layout(std140, ACDREAM_UBO_SET binding = 1) uniform SceneLighting {
    Light uLights[8];
    vec4  uCellAmbient;
    vec4  uFogParams;
    vec4  uFogColor;
    vec4  uCameraAndTime;
};


uint nsPcg(uint v)
{
    v = v * 747796405u + 2891336453u;
    v = ((v >> ((v >> 28u) + 4u)) ^ v) * 277803737u;
    return (v >> 22u) ^ v;
}

float nsRand(uint s) { return float(s) * (1.0 / 4294967296.0); }

vec3 nsTint(float r)
{
    if (r < 0.04) return vec3(1.00, 0.55, 0.35);   // orange-red
    if (r < 0.12) return vec3(1.00, 0.80, 0.60);   // warm
    if (r < 0.62) return vec3(1.00, 0.97, 0.93);   // near-white
    if (r < 0.88) return vec3(0.90, 0.95, 1.00);   // blue-white
    return vec3(0.70, 0.82, 1.00);                 // hot blue
}

uint nsCellHash(ivec3 c, uint seed)
{
    uint h = uint(c.x + 1024) * 0x8DA6B343u
           ^ uint(c.y + 1024) * 0xD8163841u
           ^ uint(c.z + 1024) * 0xCB1AB31Fu
           ^ seed;
    return nsPcg(h);
}

float nsVnoise(vec3 p, uint seed)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    ivec3 ii = ivec3(i);
    float acc = 0.0;
    for (int c = 0; c < 8; ++c)
    {
        ivec3 o = ivec3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
        float v = nsRand(nsCellHash(ii + o, seed));
        vec3 wf = mix(1.0 - f, f, vec3(o));   // trilinear weights
        acc += v * wf.x * wf.y * wf.z;
    }
    return acc;
}

vec3 nsStarTier(vec3 dir, vec3 ex, vec3 ey, uint seed, float cells,
                float density, float bMin, float bMax, float sizePx,
                float haloAmp)
{
    vec3 g = dir * cells;

    float a = dot(ex, ex);
    float b = dot(ex, ey);
    float c = dot(ey, ey);
    float det = max(a * c - b * b, 1e-14);

    ivec3 base = ivec3(floor(g - 0.5));

    vec3 acc = vec3(0.0);
    for (int ci = 0; ci < 8; ++ci)
    {
        ivec3 cc = base + ivec3(ci & 1, (ci >> 1) & 1, (ci >> 2) & 1);
        uint h = nsCellHash(cc, seed ^ uint(cells));
        if (float(h & 0xFFu) > density * 255.0) continue;
        vec3 jitter = vec3(
            float((h >> 8) & 0xFFu),
            float((h >> 16) & 0xFFu),
            float((h >> 24) & 0xFFu)) * (1.0 / 255.0);
        vec3 sdir = normalize(vec3(cc) + jitter);
        vec3 v = sdir - dir * dot(sdir, dir);   // tangent-plane offset
        float bx = dot(v, ex);
        float by = dot(v, ey);
        vec2 sPx = vec2(bx * c - by * b, by * a - bx * b) / det;
        float d2 = dot(sPx, sPx);
        if (d2 > 400.0) continue;
        uint h2 = nsPcg(h);
        float t = float(h2 & 0xFFFFu) * (1.0 / 65535.0);
        float br = mix(bMin, bMax, t * t * t);
        vec3 tint = nsTint(float((h2 >> 16) & 0xFFu) * (1.0 / 255.0));
        float star = exp(-d2 / (2.0 * sizePx * sizePx));
        star += haloAmp * exp(-d2 / (24.0 * sizePx * sizePx));
        acc += br * tint * star;
    }
    return acc;
}

vec3 nightSky(vec3 dir, uint seed)
{
    vec3 ex = dFdx(dir);
    vec3 ey = dFdy(dir);

    float n = 0.62 * nsVnoise(dir * 3.0, seed ^ 0x9E3779B9u)
            + 0.38 * nsVnoise(dir * 7.0, seed ^ 0x85EBCA6Bu);
    vec3 rgb = (0.004 + 0.009 * n) * vec3(0.85, 0.92, 1.10);

    rgb += nsStarTier(dir, ex, ey, seed ^ 0x1B873593u, 110.0, 0.85, 0.05, 0.35, 0.50, 0.0);
    rgb += nsStarTier(dir, ex, ey, seed ^ 0xCC9E2D51u, 48.0, 0.45, 0.20, 0.70, 0.65, 0.0);
    rgb += nsStarTier(dir, ex, ey, seed ^ 0x27D4EB2Fu, 18.0, 0.30, 0.50, 1.40, 0.90, 0.05);
    rgb += nsStarTier(dir, ex, ey, seed ^ 0x165667B1u, 6.0, 0.08, 2.00, 4.00, 1.40, 0.10);
    return rgb;
}
// ============================================================================

void main() {
    if (uParamA > 0.5) {
        float dayL = dot(vTint, vec3(0.299, 0.587, 0.114));
        float night = 1.0 - smoothstep(0.18, 0.45, dayL);
        if (night < 0.004) {
            fragColor = vec4(0.0, 0.0, 0.0, 1.0);
            return;
        }
        vec3 sky = nightSky(normalize(vDir), uint(uParamB));
        // Additive pipeline (forced for this draw): rgb adds over the dome.
        fragColor = vec4(sky * night, 1.0);
        return;
    }

    vec4 sampled = ACDREAM_SAMPLE_2D(uTextureIndexA, vTex);

    vec3 rgb = sampled.rgb * vTint;

    int fogMode = int(uFogParams.w);
    if (uApplyFog > 0.5 && fogMode != 0) {
        rgb = mix(uFogColor.rgb, rgb, vFogFactor);
    }

    float flash = uFogParams.z;
    rgb += flash * vec3(1.5, 1.5, 1.8);

    float cap = mix(1.0, 3.0, clamp(flash, 0.0, 1.0));
    rgb = min(rgb, vec3(cap));

    float a = sampled.a * (1.0 - uTransparency) * uSurfOpacity;
    if (a < 0.01) discard;
    fragColor = vec4(rgb, a);
}
