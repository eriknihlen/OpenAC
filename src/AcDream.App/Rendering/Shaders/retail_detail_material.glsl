struct RetailDetailMaterialResult {
    vec3 rgb;
    float alpha;
};

RetailDetailMaterialResult acdreamRetailDetailMaterial(
    vec3 baseRgb,
    vec3 diffuseRgb,
    vec4 detail,
    float materialAlpha)
{
    float weight = materialAlpha * detail.a;
    return RetailDetailMaterialResult(
        detail.rgb * weight + (baseRgb * diffuseRgb) * (1.0 - weight),
        materialAlpha * detail.a * detail.a);
}
