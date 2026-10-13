// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IntroSkipper.Tests;

public class TestPlaybackOutroTailFilterConvention
{
    [Fact]
    public void NativePlaybackGetIsMatched()
    {
        var model = PlaybackFilterTestHelpers.CreateModel(typeof(Jellyfin.Api.Controllers.MediaSegmentsController));
        new PlaybackOutroTailFilterConvention().Apply(model);
        var filter = Assert.IsType<ServiceFilterAttribute>(Assert.Single(model.Controllers[0].Actions[0].Filters));
        Assert.Equal(typeof(PlaybackOutroTailFilter), filter.ServiceType);
    }

    [Theory]
    [InlineData(typeof(PlaybackFixtures.OtherMediaSegmentsController))]
    [InlineData(typeof(PlaybackFixtures.SegmentEditorController))]
    public void SimilarControllerAndEditorAreNotMatched(Type controllerType)
    {
        var model = PlaybackFilterTestHelpers.CreateModel(controllerType);
        new PlaybackOutroTailFilterConvention().Apply(model);
        Assert.Empty(model.Controllers[0].Actions[0].Filters);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void NonGetSelectorsAreNotMatched(string method)
    {
        var model = PlaybackFilterTestHelpers.CreateModel(typeof(Jellyfin.Api.Controllers.MediaSegmentsController), httpMethod: method);
        new PlaybackOutroTailFilterConvention().Apply(model);
        Assert.Empty(model.Controllers[0].Actions[0].Filters);
    }

    [Fact]
    public void DifferentActionAndMissingHttpConstraintAreNotMatched()
    {
        var model = PlaybackFilterTestHelpers.CreateModel(typeof(Jellyfin.Api.Controllers.MediaSegmentsController), "SaveSegments");
        new PlaybackOutroTailFilterConvention().Apply(model);
        Assert.Empty(model.Controllers[0].Actions[0].Filters);

        model = PlaybackFilterTestHelpers.CreateModel(typeof(Jellyfin.Api.Controllers.MediaSegmentsController));
        model.Controllers[0].Actions[0].Selectors[0].ActionConstraints.Clear();
        new PlaybackOutroTailFilterConvention().Apply(model);
        Assert.Empty(model.Controllers[0].Actions[0].Filters);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("renamed")]
    [InlineData("string")]
    [InlineData("nullable")]
    public void GuidItemIdParameterIsRequired(string mutation)
    {
        var model = PlaybackFilterTestHelpers.CreateModel(typeof(Jellyfin.Api.Controllers.MediaSegmentsController));
        var action = model.Controllers[0].Actions[0];
        if (mutation == "missing")
        {
            action.Parameters.Clear();
        }
        else if (mutation == "renamed")
        {
            action.Parameters[0].ParameterName = "id";
        }
        else
        {
            var method = typeof(ParameterFixtures).GetMethod(mutation == "string" ? nameof(ParameterFixtures.StringId) : nameof(ParameterFixtures.NullableId))!;
            action.Parameters.Clear();
            action.Parameters.Add(new ParameterModel(method.GetParameters()[0], []) { ParameterName = "itemId", Action = action });
        }

        new PlaybackOutroTailFilterConvention().Apply(model);
        Assert.Empty(action.Filters);
    }

    [Fact]
    public void ExistingFirstEpisodeFilterIsRetained()
    {
        var model = PlaybackFilterTestHelpers.CreateModel(typeof(Jellyfin.Api.Controllers.MediaSegmentsController));
        new MediaSegmentsFilterConvention().Apply(model);
        new PlaybackOutroTailFilterConvention().Apply(model);
        Assert.Equal(new[] { typeof(MediaSegmentsFirstEpisodeFilter), typeof(PlaybackOutroTailFilter) },
            model.Controllers[0].Actions[0].Filters.OfType<ServiceFilterAttribute>().Select(filter => filter.ServiceType));
    }

    [Fact]
    public void RegistrationHasIndependentSingletonAndConvention()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(EntrypointTestHelpers.CreateLibraryManager());
        new PluginServiceRegistrator().RegisterServices(services, null!);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PlaybackOutroTailFilter)).Lifetime);
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<PlaybackOutroTailFilter>(), provider.GetRequiredService<PlaybackOutroTailFilter>());
        var conventions = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Conventions;
        Assert.Single(conventions.OfType<PlaybackOutroTailFilterConvention>());
        Assert.Single(conventions.OfType<MediaSegmentsFilterConvention>());
    }

    public static class ParameterFixtures
    {
        public static void StringId(string itemId) { }

        public static void NullableId(Guid? itemId) { }
    }
}
