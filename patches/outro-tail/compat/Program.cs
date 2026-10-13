using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

var report = new Dictionary<string, object?> { ["checked_at_utc"] = DateTimeOffset.UtcNow };
try
{
    var pluginPath = Path.GetFullPath(args[0]);
    var hostPath = Path.GetFullPath(args[1]);
    var packagePath = Path.GetFullPath(args[2]);
    var loaded = new Dictionary<string, string>();
    bool IsHost(string name) => name.StartsWith("Jellyfin.", StringComparison.Ordinal) || name.StartsWith("MediaBrowser.", StringComparison.Ordinal) || name.StartsWith("Emby.", StringComparison.Ordinal);

    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var candidate = Path.Combine(hostPath, name.Name + ".dll");
        if (File.Exists(candidate))
        {
            loaded[name.Name!] = candidate;
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
        }

        if (IsHost(name.Name!))
        {
            throw new FileNotFoundException("Actual host reference missing; refusing a NuGet substitute: " + name.Name, candidate);
        }

        var candidates = Directory.EnumerateFiles(packagePath, name.Name + ".dll", SearchOption.AllDirectories)
            .Where(path => path.Contains("/lib/", StringComparison.Ordinal) && !path.Contains("/ref/", StringComparison.Ordinal))
            .Select(path => (Path: path, Identity: AssemblyName.GetAssemblyName(path)))
            .Where(entry => entry.Identity.Version == name.Version)
            .OrderByDescending(entry => entry.Path.Contains("/net10.0/", StringComparison.Ordinal))
            .ThenByDescending(entry => entry.Path.Contains("/net8.0/", StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        loaded[name.Name!] = candidates[0].Path;
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidates[0].Path);
    };

    Assembly Host(string file) => AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostPath, file + ".dll"));
    var modelAssembly = Host("MediaBrowser.Model");
    var controllerAssembly = Host("MediaBrowser.Controller");
    Host("MediaBrowser.Common");
    var databaseAssembly = Host("Jellyfin.Database.Implementations");
    var apiAssembly = Host("Jellyfin.Api");
    var pluginAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
    var resourceNames = pluginAssembly.GetManifestResourceNames();
    var pluginType = pluginAssembly.GetType("IntroSkipper.Plugin", true)!;
    var plugin = RuntimeHelpers.GetUninitializedObject(pluginType);
    var configType = pluginAssembly.GetType("IntroSkipper.Configuration.PluginConfiguration", true)!;
    var config = Activator.CreateInstance(configType)!;
    Set(config, "IntroEndOffset", 5);
    Set(config, "SkipFirstEpisode", false);
    Set(plugin, "Configuration", config);
    pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, plugin);

    var pluginId = (Guid)pluginType.GetProperty("Id")!.GetValue(plugin)!;
    Require(pluginId == Guid.Parse("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b"), "Plugin GUID changed");
    Require(pluginAssembly.GetName().Version == new Version(12, 0, 4, 0), "Plugin assembly version changed");
    foreach (var name in new[] { "configPage.html", "introskipper.js", "introskipper.css" })
    {
        var resource = resourceNames.SingleOrDefault(resource => resource.EndsWith("." + name, StringComparison.Ordinal));
        Require(resource is not null, "Missing embedded resource: " + name);
        using var stream = pluginAssembly.GetManifestResourceStream(resource!);
        Require(stream?.Length > 0, "Empty embedded resource: " + name);
    }

    var dtoType = modelAssembly.GetType("MediaBrowser.Model.MediaSegments.MediaSegmentDto", true)!;
    var queryType = modelAssembly.GetType("MediaBrowser.Model.Querying.QueryResult`1", true)!.MakeGenericType(dtoType);
    var segmentType = databaseAssembly.GetType("Jellyfin.Database.Implementations.Enums.MediaSegmentType", true)!;
    var itemId = Guid.NewGuid();
    var itemType = controllerAssembly.GetType("MediaBrowser.Controller.Entities.TV.Episode", true)!;
    var item = Activator.CreateInstance(itemType)!;
    Set(item, "Id", itemId);
    Set(item, "RunTimeTicks", 100L * TimeSpan.TicksPerSecond);
    var libraryType = controllerAssembly.GetType("MediaBrowser.Controller.Library.ILibraryManager", true)!;
    var library = DispatchProxy.Create(libraryType, typeof(LocalLibraryProxy));
    ((LocalLibraryProxy)library).Item = item;
    ((LocalLibraryProxy)library).ItemId = itemId;

    var filterType = pluginAssembly.GetType("IntroSkipper.Filters.PlaybackOutroTailFilter", true)!;
    var services = new ServiceCollection();
    services.AddSingleton(libraryType, library);
    services.AddSingleton(filterType);
    using var provider = services.BuildServiceProvider();
    var filter = (IAsyncResultFilter)provider.GetRequiredService(filterType);
    Require(ReferenceEquals(filter, provider.GetRequiredService(filterType)), "Filter is not a singleton");
    var conventionType = pluginAssembly.GetType("IntroSkipper.Filters.PlaybackOutroTailFilterConvention", true)!;
    var convention = (IApplicationModelConvention)Activator.CreateInstance(conventionType, nonPublic: true)!;
    var nativeType = apiAssembly.GetType("Jellyfin.Api.Controllers.MediaSegmentsController", true)!;
    var nativeMethod = nativeType.GetMethod("GetItemSegments")!;
    var controller = new ControllerModel(nativeType.GetTypeInfo(), nativeType.GetCustomAttributes(true));
    var action = new ActionModel(nativeMethod, nativeMethod.GetCustomAttributes(true)) { Controller = controller, ActionName = nativeMethod.Name };
    foreach (var parameter in nativeMethod.GetParameters())
    {
        action.Parameters.Add(new ParameterModel(parameter, parameter.GetCustomAttributes(true)) { Action = action, ParameterName = parameter.Name! });
    }

    foreach (var attribute in nativeMethod.GetCustomAttributes(true).OfType<HttpMethodAttribute>())
    {
        var selector = new SelectorModel();
        selector.ActionConstraints.Add(new HttpMethodActionConstraint(attribute.HttpMethods));
        action.Selectors.Add(selector);
    }

    controller.Actions.Add(action);
    var application = new ApplicationModel();
    application.Controllers.Add(controller);
    convention.Apply(application);
    Require(action.Filters.OfType<ServiceFilterAttribute>().Count(attribute => attribute.ServiceType == filterType) == 1, "Real host playback action was not matched");
    action.Selectors.Clear();
    var postSelector = new SelectorModel();
    postSelector.ActionConstraints.Add(new HttpMethodActionConstraint(["POST"]));
    action.Selectors.Add(postSelector);
    action.Filters.Clear();
    convention.Apply(application);
    Require(action.Filters.Count == 0, "POST action was incorrectly matched");

    var cases = new List<object>();
    foreach (var endSeconds in new long[] { 100, 98, 103, 95, 90 })
    {
        var dto = Activator.CreateInstance(dtoType)!;
        Set(dto, "Id", Guid.NewGuid());
        Set(dto, "ItemId", itemId);
        Set(dto, "Type", Enum.Parse(segmentType, "Outro"));
        Set(dto, "StartTicks", 10L * TimeSpan.TicksPerSecond);
        Set(dto, "EndTicks", endSeconds * TimeSpan.TicksPerSecond);
        var items = Array.CreateInstance(dtoType, 1);
        items.SetValue(dto, 0);
        var original = Activator.CreateInstance(queryType)!;
        Set(original, "Items", items);
        Set(original, "StartIndex", 7);
        Set(original, "TotalRecordCount", 23);
        var result = new OkObjectResult(original);
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        var route = new RouteData();
        route.Values["itemId"] = itemId.ToString();
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = nativeType.GetTypeInfo(), ActionName = nativeMethod.Name, MethodInfo = nativeMethod };
        var context = new ResultExecutingContext(new ActionContext(http, route, descriptor, new ModelStateDictionary()), [], result, new object());
        var calls = 0;
        await filter.OnResultExecutionAsync(context, () =>
        {
            calls++;
            return Task.FromResult(new ResultExecutedContext(context, [], result, context.Controller));
        });
        var output = result.Value!;
        var outputItems = ((System.Collections.IEnumerable)queryType.GetProperty("Items")!.GetValue(output)!).Cast<object>().ToArray();
        var expectedEnd = Math.Min(endSeconds, 95) * TimeSpan.TicksPerSecond;
        Require(calls == 1, "next was not called exactly once");
        Require(outputItems.Length == 1, "Unexpected result count");
        Require((long)dtoType.GetProperty("EndTicks")!.GetValue(outputItems[0])! == expectedEnd, "Incorrect end using real host DTO");
        Require((long)dtoType.GetProperty("EndTicks")!.GetValue(dto)! == endSeconds * TimeSpan.TicksPerSecond, "Original host DTO was mutated");
        Require((int)queryType.GetProperty("StartIndex")!.GetValue(output)! == 7, "StartIndex changed");
        Require((int)queryType.GetProperty("TotalRecordCount")!.GetValue(output)! == 23, "TotalRecordCount changed");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(output, queryType, SmokeJson.Response));
        Require(json.RootElement.GetProperty("Items")[0].GetProperty("EndTicks").GetInt64() == expectedEnd, "Serialized host response has incorrect end");
        cases.Add(new { original_end_seconds = endSeconds, response_end_ticks = expectedEnd, next_calls = calls, original_dto_unchanged = true });
    }

    var hostAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => IsHost(assembly.GetName().Name!)).ToArray();
    foreach (var assembly in hostAssemblies)
    {
        Require(assembly.Location.StartsWith(hostPath + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Host dependency was loaded from another directory: " + assembly.FullName);
    }

    report["status"] = "passed";
    report["plugin"] = new { path = pluginPath, identity = pluginAssembly.FullName, guid = pluginId, file_version = FileVersionInfo.GetVersionInfo(pluginPath).FileVersion, resources = resourceNames };
    report["real_host_controller"] = nativeType.FullName;
    report["cases"] = cases;
    report["host_assemblies"] = hostAssemblies.Select(assembly => new { identity = assembly.FullName, path = assembly.Location }).ToArray();
    report["resolved_support_dependencies"] = loaded;
    report["nas_service_started_or_changed"] = false;
    report["infuse_playback_verified"] = false;
    Console.WriteLine(JsonSerializer.Serialize(report, SmokeJson.Report));
}
catch (Exception exception)
{
    report["status"] = "failed";
    report["exception"] = exception.ToString();
    Console.WriteLine(JsonSerializer.Serialize(report, SmokeJson.Report));
    Environment.ExitCode = 1;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Set(object instance, string property, object? value)
{
    var member = instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    member.SetValue(instance, value);
}

public class LocalLibraryProxy : DispatchProxy
{
    public object? Item { get; set; }
    public Guid ItemId { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetItemById" && args?.OfType<Guid>().FirstOrDefault() == ItemId)
        {
            return Item;
        }

        throw new InvalidOperationException("Unexpected library access: " + targetMethod?.Name);
    }
}

internal static class SmokeJson
{
    internal static readonly JsonSerializerOptions Response = new() { Converters = { new JsonStringEnumConverter() } };
    internal static readonly JsonSerializerOptions Report = new() { WriteIndented = true };
}
