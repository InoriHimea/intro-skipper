// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntroSkipper.Tests
{
    internal static class PlaybackFilterTestHelpers
    {
        internal static ApplicationModel CreateModel(Type controllerType, string methodName = "GetItemSegments", string httpMethod = "GET")
        {
            var method = controllerType.GetMethod(methodName)!;
            var controller = new ControllerModel(controllerType.GetTypeInfo(), controllerType.GetCustomAttributes().ToArray())
            {
                ControllerName = controllerType.Name.Replace("Controller", string.Empty, StringComparison.Ordinal)
            };
            var action = new ActionModel(method, method.GetCustomAttributes().ToArray())
            {
                ActionName = method.Name,
                Controller = controller
            };
            foreach (var parameter in method.GetParameters())
            {
                action.Parameters.Add(new ParameterModel(parameter, parameter.GetCustomAttributes().ToArray())
                {
                    ParameterName = parameter.Name!,
                    Action = action
                });
            }

            var selector = new SelectorModel();
            selector.ActionConstraints.Add(new HttpMethodActionConstraint([httpMethod]));
            action.Selectors.Add(selector);
            controller.Actions.Add(action);
            var model = new ApplicationModel();
            model.Controllers.Add(controller);
            return model;
        }

        internal static ResultExecutingContext CreateContext(
            Guid itemId,
            IActionResult result,
            string httpMethod = "GET",
            bool includeItemId = true,
            Type? controllerType = null)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = httpMethod;
            var route = new RouteData();
            if (includeItemId)
            {
                route.Values["itemId"] = itemId.ToString();
            }

            var type = controllerType ?? typeof(Jellyfin.Api.Controllers.MediaSegmentsController);
            var descriptor = new ControllerActionDescriptor
            {
                ControllerTypeInfo = type.GetTypeInfo(),
                ControllerName = type.Name.Replace("Controller", string.Empty, StringComparison.Ordinal),
                ActionName = "GetItemSegments",
                MethodInfo = type.GetMethod("GetItemSegments")!
            };
            var actionContext = new ActionContext(http, route, descriptor, new ModelStateDictionary());
            return new ResultExecutingContext(actionContext, [], result, Activator.CreateInstance(type)!);
        }

        internal static async Task RunAsync(ResultExecutingContext context, params IAsyncResultFilter[] filters)
        {
            Task<ResultExecutedContext> Next(int index)
            {
                if (index == filters.Length)
                {
                    return Task.FromResult(new ResultExecutedContext(context, [], context.Result, context.Controller));
                }

                return RunFilter(index);
            }

            async Task<ResultExecutedContext> RunFilter(int index)
            {
                ResultExecutedContext? result = null;
                await filters[index].OnResultExecutionAsync(context, async () =>
                {
                    result = await Next(index + 1);
                    return result;
                });
                return result!;
            }

            await Next(0);
        }

        internal static async Task RunRegisteredFiltersAsync(ResultExecutingContext context, ILibraryManager libraryManager)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(libraryManager);
            new PluginServiceRegistrator().RegisterServices(services, null!);
            using var provider = services.BuildServiceProvider();
            var model = CreateModel(typeof(Jellyfin.Api.Controllers.MediaSegmentsController));
            foreach (var convention in provider.GetRequiredService<IOptions<MvcOptions>>().Value.Conventions)
            {
                convention.Apply(model);
            }

            var filters = model.Controllers[0].Actions[0].Filters
                .OfType<ServiceFilterAttribute>()
                .Select(factory => (IAsyncResultFilter)factory.CreateInstance(provider))
                .ToArray();
            await RunAsync(context, filters);
        }

        internal static MediaSegmentDto Segment(Guid itemId, long startTicks, long endTicks, MediaSegmentType type = MediaSegmentType.Outro)
            => new()
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Type = type,
                StartTicks = startTicks,
                EndTicks = endTicks
            };
    }
}

namespace Jellyfin.Api.Controllers
{
    // Metadata fixture only; the compatibility harness separately loads the real host controller.
    public sealed class MediaSegmentsController : ControllerBase
    {
        [HttpGet("{itemId}")]
        public IActionResult GetItemSegments(Guid itemId) => Ok();

        [HttpPost("{itemId}")]
        public IActionResult SaveSegments(Guid itemId) => Ok();
    }
}

namespace IntroSkipper.Tests.PlaybackFixtures
{
    public sealed class OtherMediaSegmentsController : ControllerBase
    {
        [HttpGet("{itemId}")]
        public IActionResult GetItemSegments(Guid itemId) => Ok();
    }

    public sealed class SegmentEditorController : ControllerBase
    {
        [HttpGet("{itemId}")]
        public IActionResult GetItemSegments(Guid itemId) => Ok();
    }
}
