#ifndef ACDREAM_ATMOSPHERIC_COMMON_GLSL
#define ACDREAM_ATMOSPHERIC_COMMON_GLSL

layout(std140, ACDREAM_PACK_UBO_SET binding = 5) uniform AtmosphericFrame {
    vec4 uAtmosphereSunScreen; //  0: uv.xy, ray strength, elevation degrees
    vec4 uAtmosphereSunColor;
    vec4 uAtmosphereViewport;  // 32: width, height, reciprocal width/height
    vec4 uAtmosphereWeather;   // 48: kind, intensity, delta seconds, outdoor
    vec4 uAtmosphereSunDirection; // 64: surface-to-sun xyz, authored brightness
    vec4 uAtmospherePolicy;    // 80: day group, group factor, shadow/shaft elevation factors
    mat4 uAtmosphereInverseViewProjection; // 96: screen/depth to world
    vec4 uAtmosphereClockWind;      // 160: elapsed seconds, wind mean [0..1], wind gust [0..1], wind direction radians
    vec4 uAtmosphereWindAmplitude;
};

layout(std140, ACDREAM_PACK_UBO_SET binding = 7) uniform PackPass {
    vec4 uPackParams0; //  0
    vec4 uPackParams1; // 16
    vec4 uPackParams2; // 32
    vec4 uPackParams3; // 48
};

// FusedAtmosphericPostProcess PackPass ABI (opt-in Low preset only):
//   sun-rays:        Params1 = (enabled, logical mask width, mask height, 0)
//   filmic:          Params1.z = enabled; Params2 = bloom extraction parameters;
//                    Params3.xy = logical bloom texel step

layout(std140, ACDREAM_PACK_UBO_SET binding = 8) uniform PackSettings {
    vec4 uPackSettings[16]; // 64 declaration-order scalar setting slots
};

vec3 acdreamDecodeDisplay(vec3 c)
{
    return pow(max(c, vec3(0.0)), vec3(2.2));
}

vec3 acdreamEncodeDisplay(vec3 c)
{
    return pow(max(c, vec3(0.0)), vec3(1.0 / 2.2));
}

#endif
