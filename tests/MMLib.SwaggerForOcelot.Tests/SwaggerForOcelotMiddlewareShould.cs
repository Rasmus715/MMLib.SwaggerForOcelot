﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JsonDiffPatchDotNet;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MMLib.SwaggerForOcelot.Configuration;
using MMLib.SwaggerForOcelot.Middleware;
using MMLib.SwaggerForOcelot.Repositories;
using MMLib.SwaggerForOcelot.Transformation;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;
using Xunit.Sdk;
using Options = Microsoft.Extensions.Options.Options;

namespace MMLib.SwaggerForOcelot.Tests
{
    public class SwaggerForOcelotMiddlewareShould
    {
        [Fact]
        public async Task AllowVersionPlaceholder()
        {
            // Arrange
            const string version = "v1";
            const string key = "projects";
            HttpContext httpContext = GetHttpContext(requestPath: $"/{version}/{key}");
            IMemoryCache memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

            var next = new TestRequestDelegate();

            // What is being tested
            var swaggerForOcelotOptions = new SwaggerForOcelotUIOptions();
            TestSwaggerEndpointOptions swaggerEndpointOptions = CreateSwaggerEndpointOptions(key, version);
            var routeOptions = new TestRouteOptions(new List<RouteOptions>
            {
                new()
                {
                    SwaggerKey = "projects",
                    UpstreamPathTemplate = "/api/{version}/projects/Values/{everything}",
                    DownstreamPathTemplate = "/api/{version}/Values/{everything}",
                },
                new()
                {
                    SwaggerKey = "projects",
                    UpstreamPathTemplate = "/api/projects/Projects",
                    DownstreamPathTemplate = "/api/Projects",
                },
                new()
                {
                    SwaggerKey = "projects",
                    UpstreamPathTemplate = "/api/projects/Projects/{everything}",
                    DownstreamPathTemplate = "/api/Projects/{everything}",
                }
            });

            // downstreamSwagger is returned when client.GetStringAsync is called by the middleware.
            string downstreamSwagger = await GetBaseOpenApi("OpenApiWithVersionPlaceholderBase");
            HttpClient httClientMock = GetHttpClient(downstreamSwagger);
            var httpClientFactory = new TestHttpClientFactory(httClientMock);

            // upstreamSwagger is returned after swaggerJsonTransformer transforms the downstreamSwagger
            string expectedSwagger = await GetBaseOpenApi("OpenApiWithVersionPlaceholderBaseTransformed");

            var swaggerJsonTransformerMock = new Mock<ISwaggerJsonTransformer>();
            swaggerJsonTransformerMock
                .Setup(x => x.Transform(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<RouteOptions>>(),
                    It.IsAny<string>(),
                    It.IsAny<SwaggerEndPointOptions>()))
                .Returns((
                    string swaggerJson,
                    IEnumerable<RouteOptions> routeOptions,
                    string serverOverride,
                    SwaggerEndPointOptions options) => new SwaggerJsonTransformer(OcelotSwaggerGenOptions.Default, memoryCache)
                    .Transform(swaggerJson, routeOptions, serverOverride, options));
            var swaggerForOcelotMiddleware = new SwaggerForOcelotMiddleware(
                next.Invoke,
                swaggerForOcelotOptions,
                routeOptions,
                swaggerJsonTransformerMock.Object,
                Substitute.For<ISwaggerProvider>());

            // Act
            await swaggerForOcelotMiddleware.Invoke(
                httpContext,
                new SwaggerEndPointProvider(swaggerEndpointOptions, OcelotSwaggerGenOptions.Default),
                new DownstreamSwaggerDocsRepository(Microsoft.Extensions.Options.Options.Create(swaggerForOcelotOptions),
                    httpClientFactory, DummySwaggerServiceDiscoveryProvider.Default));
            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);

            // Assert
            using (var streamReader = new StreamReader(httpContext.Response.Body))
            {
                string transformedUpstreamSwagger = await streamReader.ReadToEndAsync();
                AreEqual(transformedUpstreamSwagger, expectedSwagger);
            }

            swaggerJsonTransformerMock.Verify(x => x.Transform(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<RouteOptions>>(),
                It.IsAny<string>(),
                It.IsAny<SwaggerEndPointOptions>()), Times.Once);
        }

        [Fact]
        public async Task AllowUserDefinedUpstreamTransformer()
        {
            // Arrange
            const string version = "v1";
            const string key = "projects";
            HttpContext httpContext = GetHttpContext(requestPath: $"/{version}/{key}");

            var next = new TestRequestDelegate();

            // What is being tested
            var swaggerForOcelotOptions = new SwaggerForOcelotUIOptions()
            {
                ReConfigureUpstreamSwaggerJson = ExampleUserDefinedUpstreamTransformer
            };
            TestSwaggerEndpointOptions testSwaggerEndpointOptions = CreateSwaggerEndpointOptions(key, version);
            var routeOptions = new TestRouteOptions();

            // downstreamSwagger is returned when client.GetStringAsync is called by the middleware.
            string downstreamSwagger = await GetBaseOpenApi("OpenApiBase");
            HttpClient httClientMock = GetHttpClient(downstreamSwagger);
            var httpClientFactory = new TestHttpClientFactory(httClientMock);

            // upstreamSwagger is returned after swaggerJsonTransformer transforms the downstreamSwagger
            string upstreamSwagger = await GetBaseOpenApi("OpenApiBaseTransformed");
            var swaggerJsonTransformer = new TestSwaggerJsonTransformer(upstreamSwagger);

            var swaggerForOcelotMiddleware = new SwaggerForOcelotMiddleware(
                next.Invoke,
                swaggerForOcelotOptions,
                routeOptions,
                swaggerJsonTransformer,
                Substitute.For<ISwaggerProvider>());

            // Act
            await swaggerForOcelotMiddleware.Invoke(
                httpContext,
                new SwaggerEndPointProvider(testSwaggerEndpointOptions, OcelotSwaggerGenOptions.Default),
                new DownstreamSwaggerDocsRepository(Microsoft.Extensions.Options.Options.Create(swaggerForOcelotOptions),
                    httpClientFactory, DummySwaggerServiceDiscoveryProvider.Default));
            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            string transformedUpstreamSwagger = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();

            // Assert
            AreEqual(transformedUpstreamSwagger, upstreamSwagger);
        }

        [Fact]
        public async Task RespectDownstreamHttpVersionRouteSetting()
        {
            // Arrange
            const string version = "v1";
            const string key = "projects";
            HttpContext httpContext = GetHttpContext(requestPath: $"/{version}/{key}");
            IMemoryCache memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

            var next = new TestRequestDelegate();

            // What is being tested
            var swaggerForOcelotOptions = new SwaggerForOcelotUIOptions();
            TestSwaggerEndpointOptions swaggerEndpointOptions = CreateSwaggerEndpointOptions(key, version);
            var routeOptions = new TestRouteOptions(new List<RouteOptions>
            {
                new()
                {
                    SwaggerKey = "projects",
                    UpstreamPathTemplate = "/api/projects/Projects",
                    DownstreamPathTemplate = "/api/Projects",
                    DownstreamHttpVersion = "2.0",
                }
            });

            // downstreamSwagger is returned when client.GetStringAsync is called by the middleware.
            string downstreamSwagger = await GetBaseOpenApi("OpenApiWithVersionPlaceholderBase");
            HttpClient httClientMock = GetHttpClient(downstreamSwagger);
            var httpClientFactory = new TestHttpClientFactory(httClientMock);

            var swaggerJsonTransformerMock = new Mock<ISwaggerJsonTransformer>();
            swaggerJsonTransformerMock
                .Setup(x => x.Transform(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<RouteOptions>>(),
                    It.IsAny<string>(),
                    It.IsAny<SwaggerEndPointOptions>()))
                .Returns((
                    string swaggerJson,
                    IEnumerable<RouteOptions> routeOptions,
                    string serverOverride,
                    SwaggerEndPointOptions options) => new SwaggerJsonTransformer(OcelotSwaggerGenOptions.Default, memoryCache)
                    .Transform(swaggerJson, routeOptions, serverOverride, options));
            var swaggerForOcelotMiddleware = new SwaggerForOcelotMiddleware(
                next.Invoke,
                swaggerForOcelotOptions,
                routeOptions,
                swaggerJsonTransformerMock.Object,
                Substitute.For<ISwaggerProvider>());

            // Act
            await swaggerForOcelotMiddleware.Invoke(
                httpContext,
                new SwaggerEndPointProvider(swaggerEndpointOptions, OcelotSwaggerGenOptions.Default),
                new DownstreamSwaggerDocsRepository(Microsoft.Extensions.Options.Options.Create(swaggerForOcelotOptions),
                    httpClientFactory, DummySwaggerServiceDiscoveryProvider.Default));
            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);

            // Assert
            httClientMock.DefaultRequestVersion.Should().BeEquivalentTo(new Version(2, 0));
        }

        private static TestSwaggerEndpointOptions CreateSwaggerEndpointOptions(string key, string version)
            => new TestSwaggerEndpointOptions(
                new List<SwaggerEndPointOptions>()
                {
                    new SwaggerEndPointOptions()
                    {
                        Key = key,
                        Config = new List<SwaggerEndPointConfig>()
                        {
                            new SwaggerEndPointConfig()
                            {
                                Name = "Test Service",
                                Version = version,
                                Url = $"http://test.com/{version}/{key}/swagger.json"
                            }
                        }
                    }
                });

        [Fact]
        public void ConstructHostUriUsingUriBuilder()
        {
            // arrange
            string expectedScheme = "http";
            var expectedHostString = new HostString("localhost", 3333);

            // apply
            string absoluteHostUri = UriHelper.BuildAbsolute(expectedScheme, expectedHostString)
                .RemoveSlashFromEnd();

            // assert
            expectedHostString.ToUriComponent().Should().NotContain(expectedScheme);
            absoluteHostUri.Should().Be($"{expectedScheme}://{expectedHostString}");
        }

        /// This method removes a routes
        private string ExampleUserDefinedUpstreamTransformer(HttpContext context, string openApiJson)
        {
            if (context.Request.Path.Value != "/v1/test")
            {
                return openApiJson;
            }

            const string routeToRemove = "/api/projects/Values";

            var swagger = JObject.Parse(openApiJson);
            JToken paths = swagger[OpenApiProperties.Paths];
            JProperty pathToRemove = paths.Values<JProperty>().FirstOrDefault(c => c.Name == routeToRemove);
            pathToRemove?.Remove();
            return swagger.ToString(Formatting.Indented);
        }

        private static void AreEqual(string transformedSwagger, string expectedTransformedSwagger)
        {
            var transformedJson = JObject.Parse(transformedSwagger);
            var expectedJson = JObject.Parse(expectedTransformedSwagger);

            if (!JObject.DeepEquals(transformedJson, expectedJson))
            {
                var jdp = new JsonDiffPatch();
                JToken patch = jdp.Diff(transformedJson, expectedJson);

                throw new XunitException(
                    $"Transformed upstream swagger is not equal to expected. {Environment.NewLine} Diff: {patch}");
            }
        }

        private HttpContext GetHttpContext(string requestPath)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = requestPath;
            context.Request.Host = new HostString("test.com");
            context.Response.Body = new MemoryStream();
            return context;
        }

        private HttpClient GetHttpClient(string downstreamSwagger)
        {
            var httpMessageHandlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            httpMessageHandlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(downstreamSwagger) };
                    return response;
                });
            return new HttpClient(httpMessageHandlerMock.Object);
        }

        private async Task<string> GetBaseOpenApi(string openApiName) =>
            await AssemblyHelper.GetStringFromResourceFileAsync($"{openApiName}.json");

        private class TestRequestDelegate
        {
            public TestRequestDelegate(int statusCode = 200)
            {
                StatusCode = statusCode;
            }

            public bool Called => CalledCount > 0;

            public int CalledCount { get; private set; }

            public int StatusCode { get; }

            public Task Invoke(HttpContext context)
            {
                System.Console.WriteLine(context);
                CalledCount++;
                return Task.CompletedTask;
            }
        }

        private class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _mockHttpClient;

            public TestHttpClientFactory(HttpClient mockHttpClient)
            {
                _mockHttpClient = mockHttpClient;
            }

            public HttpClient CreateClient()
            {
                return _mockHttpClient;
            }

            public HttpClient CreateClient(string name)
            {
                return _mockHttpClient;
            }
        }

        private class TestRouteOptions : IOptionsMonitor<List<RouteOptions>>
        {
            public TestRouteOptions()
            {
                CurrentValue = new List<RouteOptions>();
            }

            public TestRouteOptions(List<RouteOptions> value)
            {
                CurrentValue = value;
            }

            public List<RouteOptions> Get(string name) => throw new NotImplementedException();

            public IDisposable OnChange(Action<List<RouteOptions>, string> listener) => throw new NotImplementedException();

            public List<RouteOptions> CurrentValue { get; }
        }

        private class TestSwaggerEndpointOptions : IOptionsMonitor<List<SwaggerEndPointOptions>>
        {
            public TestSwaggerEndpointOptions(List<SwaggerEndPointOptions> options)
            {
                CurrentValue = options;
            }

            public List<SwaggerEndPointOptions> Get(string name) => throw new NotImplementedException();

            public IDisposable OnChange(Action<List<SwaggerEndPointOptions>, string> listener) =>
                throw new NotImplementedException();

            public List<SwaggerEndPointOptions> CurrentValue { get; }
        }

        private class TestSwaggerJsonTransformer : ISwaggerJsonTransformer
        {
            private readonly string _transformedJson;

            public TestSwaggerJsonTransformer(string transformedJson)
            {
                _transformedJson = transformedJson;
            }

            public string Transform(string swaggerJson,
                IEnumerable<RouteOptions> routes,
                string serverOverride,
                SwaggerEndPointOptions endPointOptions)
            {
                return _transformedJson;
            }
        }
    }
}
