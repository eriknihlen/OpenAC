using System.Reflection;
using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public sealed class ChargenNoChoriziteLeakTests
{
    [Fact]
    public void PublicCharGenSurface_NeverExposesChoriziteOrDatReaderWriterTypes()
    {
        Assembly coreAssembly = typeof(ChargenOptions).Assembly;
        Type[] publicCharGenTypes = coreAssembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "AcDream.Core.CharGen")
            .ToArray();

        // Guards the guard: if the namespace ever ends up empty (e.g. a
        // rename), this test must fail loudly rather than vacuously pass.
        Assert.True(
            publicCharGenTypes.Length > 5,
            $"Expected multiple public types in AcDream.Core.CharGen, found {publicCharGenTypes.Length}. " +
            "Did the namespace get renamed or moved?");

        var offenders = new List<string>();

        foreach (Type type in publicCharGenTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                CheckSite(property.PropertyType, $"{type.FullName}.{property.Name} (property)", offenders);

                foreach (ParameterInfo indexParam in property.GetIndexParameters())
                {
                    CheckSite(
                        indexParam.ParameterType,
                        $"{type.FullName}.{property.Name}[{indexParam.Name}] (indexer parameter)",
                        offenders);
                }
            }

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                CheckSite(field.FieldType, $"{type.FullName}.{field.Name} (field)", offenders);
            }

            foreach (ConstructorInfo ctor in type.GetConstructors(
                BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (ParameterInfo param in ctor.GetParameters())
                {
                    CheckSite(
                        param.ParameterType,
                        $"{type.FullName}..ctor({param.Name}) (constructor parameter)",
                        offenders);
                }
            }

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.IsSpecialName)
                    continue;

                CheckSite(method.ReturnType, $"{type.FullName}.{method.Name} (return type)", offenders);

                foreach (ParameterInfo param in method.GetParameters())
                {
                    CheckSite(
                        param.ParameterType,
                        $"{type.FullName}.{method.Name}({param.Name}) (method parameter)",
                        offenders);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A Chorizite/DatReaderWriter type leaked onto a public AcDream.Core.CharGen member:\n"
            + string.Join('\n', offenders));
    }

    private static void CheckSite(Type type, string site, List<string> offenders)
    {
        foreach (Type candidate in FlattenTypeArguments(type))
        {
            string? assemblyName = candidate.Assembly.GetName().Name;
            if (assemblyName is null)
                continue;

            bool isForbidden =
                assemblyName.Equals("DatReaderWriter", StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith("Chorizite", StringComparison.OrdinalIgnoreCase);

            if (isForbidden)
                offenders.Add($"{site}: {candidate.FullName} (assembly '{assemblyName}')");
        }
    }

    private static IEnumerable<Type> FlattenTypeArguments(Type type)
    {
        yield return type;

        if (type.IsByRef || type.IsPointer)
        {
            Type? element = type.GetElementType();
            if (element is not null)
            {
                foreach (Type inner in FlattenTypeArguments(element))
                    yield return inner;
            }
            yield break;
        }

        if (type.IsArray)
        {
            Type? element = type.GetElementType();
            if (element is not null)
            {
                foreach (Type inner in FlattenTypeArguments(element))
                    yield return inner;
            }
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type inner in FlattenTypeArguments(argument))
                    yield return inner;
            }
        }
    }
}
