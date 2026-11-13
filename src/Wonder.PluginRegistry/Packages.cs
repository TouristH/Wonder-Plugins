// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wonder.Extensibility;

namespace Wonder.PluginRegistry;

public static class Packages
{
    public const long Limit = 64 * 1024 * 1024;
    public static readonly string[] Metadata = ["id", "name", "description", "author", "version", "apiVersion"];
    public static async Task<JsonObject> Inspect(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > Limit) throw new InvalidDataException("Package size is invalid");
        var root = Directory.CreateTempSubdirectory("wonder-registry-package-").FullName;
        try
        {
            var path = Path.Combine(root, "package.zip");
            await File.WriteAllBytesAsync(path, bytes);
            using var manager = new PluginManager(Path.Combine(root, "data"));
            await manager.InstallLocalAsync(path);
            var installed = manager.Plugins.Single();
            VerifyEntry(installed);
            foreach (var required in new[] { "LICENSE", "RISK.md", "permissions.json" })
                if (!File.Exists(Path.Combine(installed.Directory, required)) || new FileInfo(Path.Combine(installed.Directory, required)).Length == 0)
                    throw new InvalidDataException($"Package requires {required}");
            PluginPackageReport.Verify(installed.Directory);
            var manifest = JsonSerializer.SerializeToNode(installed.Manifest, Json.Options)!;
            var result = new JsonObject();
            foreach (var key in Metadata) result[key] = manifest[key]!.DeepClone();
            if (manifest["permissions"] is { } permissions) result["permissions"] = permissions.DeepClone();
            result["size"] = bytes.Length;
            result["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return result;
        }
        finally { Directory.Delete(root, true); }
    }
    public static void Match(JsonNode expected, JsonNode actual)
    {
        foreach (var field in Metadata.Concat(["size", "sha256", "permissions"]))
            if (!Json.Equal(expected[field], actual[field])) throw new InvalidDataException($"Package differs from declared {field}");
    }
    public static void FixedRelease(JsonNode entry, string repository)
    {
        Json.Fields(entry, Metadata.Concat(["downloadUrl","sha256","size","sourceUrl"]), "permissions");
        Json.Id(Json.Text(entry, "id")); Json.Version(Json.Text(entry, "version")); Json.Hash(Json.Text(entry, "sha256"));
        Json.Nonempty(entry,"name",256); Json.Nonempty(entry,"description",4096); Json.Nonempty(entry,"author",256);
        if (Json.Number(entry,"apiVersion") != 1) throw new InvalidDataException("Unsupported plugin API");
        if (entry["permissions"] is { } declarations)
        {
            if (declarations is not JsonArray array || array.Count > 64) throw new InvalidDataException("Invalid supplementary declarations");
            foreach (var declaration in array) { Json.Fields(declaration!,["name","description"]); Json.Nonempty(declaration!,"name",256);Json.Nonempty(declaration!,"description",4096); }
        }
        if (Json.Number(entry, "size") is <= 0 or > Limit) throw new InvalidDataException("Invalid size");
        var url = new Uri(Json.Text(entry, "downloadUrl"));
        var prefix = $"/{Json.Repository(repository)}/releases/download/";
        if (url.Scheme != "https" || url.Authority != "github.com" || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
            url.AbsolutePath[prefix.Length..].Split('/') is not [var tag, var asset] || tag.Length == 0 || tag == "latest" || !asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use a fixed GitHub Release ZIP from the publisher repository");
        if (Json.Text(entry, "sourceUrl") != $"https://github.com/{repository}") throw new InvalidDataException("Source URL must identify publisher");
    }
    private static void VerifyEntry(InstalledPlugin plugin)
    {
        // Inspect metadata only. Never load an assembly or run its initializers during intake.
        using var stream = File.OpenRead(Path.Combine(plugin.Directory,plugin.Manifest.EntryAssembly));
        using var image = new PEReader(stream);
        if (!image.HasMetadata || image.PEHeaders.CorHeader == null) throw new InvalidDataException("Entry DLL must be a managed .NET assembly");
        var metadata = image.GetMetadataReader();
        string FullName(TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            var parent = type.GetDeclaringType();
            return parent.IsNil ? (metadata.GetString(type.Namespace) is {Length: >0} ns ? ns+"." : "") + metadata.GetString(type.Name)
                : FullName(parent)+"+"+metadata.GetString(type.Name);
        }
        var found = metadata.TypeDefinitions.FirstOrDefault(handle => FullName(handle) == plugin.Manifest.EntryType);
        if (found.IsNil) throw new InvalidDataException("Declared entry type does not exist in the entry assembly");
        var definition = metadata.GetTypeDefinition(found);
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic) || (definition.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0)
            throw new InvalidDataException("Plugin entry must be a visible non-abstract class");
        if (!definition.GetMethods().Any(handle =>
        {
            var method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != ".ctor" || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) return false;
            var signature = metadata.GetBlobReader(method.Signature); var header = signature.ReadSignatureHeader();
            if (header.IsGeneric) _ = signature.ReadCompressedInteger();
            return signature.ReadCompressedInteger() == 0;
        })) throw new InvalidDataException("Plugin entry needs a public parameterless constructor");
    }
}
