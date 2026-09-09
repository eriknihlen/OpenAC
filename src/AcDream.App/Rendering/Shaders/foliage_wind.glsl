#ifndef ACDREAM_FOLIAGE_WIND_GLSL
#define ACDREAM_FOLIAGE_WIND_GLSL

vec3 acdreamFoliageDisplace(
    vec3 worldPos,
    vec3 instanceOrigin,
    uint batchFlags,
    vec4 clockWind,
    vec4 amp)
{
    if ((batchFlags & 0x6u) == 0u)
        return worldPos;

    float t = clockWind.x;
    float mean = clockWind.y;
    float gust = clockWind.z;
    float dirA = clockWind.w;

    vec2 dir = vec2(cos(dirA), sin(dirA));
    vec2 perp = vec2(-dir.y, dir.x);

    float h = clamp((worldPos.z - instanceOrigin.z) / max(amp.w, 0.5), 0.0, 1.0);
    float k = h * h;

    float ph = dot(instanceOrigin.xy, vec2(0.137, 0.291));

    // Gust envelope: two slow sines beat against each other so gusts arrive
    // and leave rather than pulsing at one fixed period.
    float g = 0.5 + 0.5 * sin(0.05 * t + ph) + 0.25 * sin(0.13 * t + 1.7 * ph);
    float s = mean + gust * g;

    // Slow whole-tree lean, height-squared scaled.
    float lean = k * amp.x * s * (0.8 + 0.2 * sin(0.35 * t + ph));
    vec2 d = dir * lean;

    if ((batchFlags & 0x2u) != 0u)
    {
        float branch = k * amp.y * s * sin(1.1 * t + ph + 2.0 * h);
        vec2 relativeXY = worldPos.xy - instanceOrigin.xy;
        float vh = fract(sin(dot(relativeXY, vec2(12.9898, 78.233))) * 43758.5453);
        float flutter = h * amp.z * s * sin(6.0 * t + 7.0 * vh);
        d += dir * branch + perp * 0.35 * branch
            + vec2(cos(6.2832 * vh), sin(6.2832 * vh)) * flutter;
    }

    vec3 p = worldPos;
    p.xy += d;
    // Bend shortens the vertical extent instead of stretching it.
    p.z -= 0.5 * dot(d, d) / max(h * amp.w, 0.5);
    return p;
}

#endif
