// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;

namespace Cvoya.Graph.PackageVersionVerifier;

internal static class Program
{
    private const string InformationalVersionAttribute = "System.Reflection.AssemblyInformationalVersionAttribute";
    private const string FileVersionAttribute = "System.Reflection.AssemblyFileVersionAttribute";
    private static readonly string[] RuntimeFrameworks = ["net10.0", "net11.0"];

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PackageVersionVerifier <package-directory> <expected-version>");
            return 64;
        }

        string packageDirectory = Path.GetFullPath(args[0]);
        string expectedVersion = args[1];
        if (!Directory.Exists(packageDirectory))
        {
            Console.Error.WriteLine($"Package directory does not exist: {packageDirectory}");
            return 1;
        }

        string numericVersion = expectedVersion.Split('-', 2)[0];
        if (!Version.TryParse($"{numericVersion}.0", out Version? expectedAssemblyVersion))
        {
            Console.Error.WriteLine($"Expected version is not a three-part semantic version: {expectedVersion}");
            return 1;
        }

        string[] packagePaths = Directory
            .EnumerateFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".nupkg", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (packagePaths.Length == 0)
        {
            Console.Error.WriteLine($"No .nupkg files found in {packageDirectory}.");
            return 1;
        }

        List<string> errors = [];
        int assemblyCount = 0;
        foreach (string packagePath in packagePaths)
        {
            VerifyPackage(packagePath, expectedVersion, expectedAssemblyVersion, errors, ref assemblyCount);
        }

        foreach (string error in errors)
        {
            Console.Error.WriteLine(error);
        }

        if (errors.Count > 0)
        {
            return 1;
        }

        Console.WriteLine(
            $"Verified package and assembly metadata for {packagePaths.Length} package(s), {assemblyCount} assembly file(s), at {expectedVersion}.");
        return 0;
    }

    private static void VerifyPackage(
        string packagePath,
        string expectedVersion,
        Version expectedAssemblyVersion,
        List<string> errors,
        ref int assemblyCount)
    {
        string packageName = Path.GetFileName(packagePath);

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            ZipArchiveEntry[] manifests = archive.Entries
                .Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (manifests.Length != 1)
            {
                errors.Add($"{packageName}: expected exactly one .nuspec manifest, found {manifests.Length}.");
                return;
            }

            (string? packageId, string? packageVersion) = ReadPackageIdentity(manifests[0]);
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(packageVersion))
            {
                errors.Add($"{packageName}: manifest is missing package id or version.");
                return;
            }

            if (!string.Equals(packageVersion, expectedVersion, StringComparison.Ordinal))
            {
                errors.Add($"{packageName}: manifest version is {packageVersion}; expected {expectedVersion}.");
            }

            string expectedPackageName = $"{packageId}.{expectedVersion}.nupkg";
            if (!string.Equals(packageName, expectedPackageName, StringComparison.Ordinal))
            {
                errors.Add($"{packageName}: filename should be {expectedPackageName} according to its manifest.");
            }

            ZipArchiveEntry[] assemblies = archive.Entries
                .Where(IsCvoyaAssembly)
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();

            if (assemblies.Length == 0)
            {
                errors.Add($"{packageName}: package contains no Cvoya.Graph*.dll assembly to verify.");
                return;
            }

            // Analyzer-only packages put their own assembly under analyzers/, while
            // runtime packages may bundle a different assembly there as a generator.
            if (archive.GetEntry($"analyzers/dotnet/cs/{packageId}.dll") is null)
            {
                foreach (string framework in RuntimeFrameworks)
                {
                    string assetPath = $"lib/{framework}/{packageId}.dll";
                    if (archive.GetEntry(assetPath) is null)
                    {
                        errors.Add($"{packageName}: missing runtime assembly {assetPath}.");
                    }
                }
            }

            foreach (ZipArchiveEntry assembly in assemblies)
            {
                VerifyAssembly(packageName, assembly, expectedVersion, expectedAssemblyVersion, errors);
                assemblyCount++;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or XmlException)
        {
            errors.Add($"{packageName}: could not inspect package: {exception.Message}");
        }
    }

    private static (string? Id, string? Version) ReadPackageIdentity(ZipArchiveEntry manifest)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        using Stream stream = manifest.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement? metadata = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "metadata");

        string? id = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value;
        string? version = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "version")?.Value;
        return (id, version);
    }

    private static bool IsCvoyaAssembly(ZipArchiveEntry entry)
    {
        string fileName = Path.GetFileName(entry.FullName);
        return fileName.StartsWith("Cvoya.Graph", StringComparison.Ordinal)
            && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyAssembly(
        string packageName,
        ZipArchiveEntry entry,
        string expectedVersion,
        Version expectedAssemblyVersion,
        List<string> errors)
    {
        try
        {
            using Stream entryStream = entry.Open();
            using MemoryStream assemblyStream = new();
            entryStream.CopyTo(assemblyStream);
            assemblyStream.Position = 0;

            using PEReader peReader = new(assemblyStream);
            if (!peReader.HasMetadata)
            {
                errors.Add($"{packageName}:{entry.FullName}: file has no .NET metadata.");
                return;
            }

            MetadataReader metadata = peReader.GetMetadataReader();
            AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
            if (assembly.Version != expectedAssemblyVersion)
            {
                errors.Add(
                    $"{packageName}:{entry.FullName}: AssemblyVersion is {assembly.Version}; expected {expectedAssemblyVersion}.");
            }

            string? informationalVersion = ReadStringAttribute(metadata, assembly, InformationalVersionAttribute);
            if (!MatchesInformationalVersion(informationalVersion, expectedVersion))
            {
                errors.Add(
                    $"{packageName}:{entry.FullName}: InformationalVersion is {informationalVersion ?? "<missing>"}; expected {expectedVersion} or {expectedVersion}+<source-revision>.");
            }

            string? fileVersion = ReadStringAttribute(metadata, assembly, FileVersionAttribute);
            if (!string.Equals(fileVersion, expectedAssemblyVersion.ToString(), StringComparison.Ordinal))
            {
                errors.Add(
                    $"{packageName}:{entry.FullName}: FileVersion is {fileVersion ?? "<missing>"}; expected {expectedAssemblyVersion}.");
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException or IOException)
        {
            errors.Add($"{packageName}:{entry.FullName}: could not inspect assembly metadata: {exception.Message}");
        }
    }

    private static bool MatchesInformationalVersion(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.Ordinal)
            || (actual?.StartsWith($"{expected}+", StringComparison.Ordinal) ?? false);

    private static string? ReadStringAttribute(
        MetadataReader metadata,
        AssemblyDefinition assembly,
        string expectedAttributeType)
    {
        foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (!string.Equals(GetAttributeTypeName(metadata, attribute.Constructor), expectedAttributeType, StringComparison.Ordinal))
            {
                continue;
            }

            BlobReader value = metadata.GetBlobReader(attribute.Value);
            return value.ReadUInt16() == 1 ? value.ReadSerializedString() : null;
        }

        return null;
    }

    private static string? GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle type = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };

        return type.Kind switch
        {
            HandleKind.TypeReference => GetFullName(metadata, metadata.GetTypeReference((TypeReferenceHandle)type)),
            HandleKind.TypeDefinition => GetFullName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)type)),
            _ => null,
        };
    }

    private static string GetFullName(MetadataReader metadata, TypeReference type)
        => JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string GetFullName(MetadataReader metadata, TypeDefinition type)
        => JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string JoinTypeName(string @namespace, string name)
        => string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
}
