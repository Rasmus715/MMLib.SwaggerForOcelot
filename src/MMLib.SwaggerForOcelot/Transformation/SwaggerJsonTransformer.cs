using Kros.IO;
using Microsoft.Extensions.Caching.Memory;
using MMLib.SwaggerForOcelot.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.SwaggerForOcelot.Transformation
{
    /// <summary>
    /// Class which implement transformation downstream service swagger json into upstream format
    /// </summary>
    /// <seealso cref="ISwaggerJsonTransformer" />
    public class SwaggerJsonTransformer : ISwaggerJsonTransformer
    {
        private readonly OcelotSwaggerGenOptions _ocelotSwaggerGenOptions;
        private readonly IMemoryCache _memoryCache;

        public SwaggerJsonTransformer(OcelotSwaggerGenOptions ocelotSwaggerGenOptions, IMemoryCache memoryCache)
        {
            _ocelotSwaggerGenOptions = ocelotSwaggerGenOptions;
            _memoryCache = memoryCache;
        }

        /// <inheritdoc/>
        public string Transform(
            string swaggerJson,
            IEnumerable<RouteOptions> routes,
            string serverOverride,
            SwaggerEndPointOptions endPointOptions)
        {
            if (_ocelotSwaggerGenOptions.DownstreamDocsCacheExpire == TimeSpan.Zero)
            {
                return TransformSwaggerOrOpenApi(swaggerJson, routes, serverOverride, endPointOptions);
            }

            return _memoryCache.GetOrCreate(
                ComputeHash(swaggerJson),
                entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = _ocelotSwaggerGenOptions.DownstreamDocsCacheExpire;
                    return TransformSwaggerOrOpenApi(swaggerJson, routes, serverOverride, endPointOptions);
                });
        }

        private string TransformSwaggerOrOpenApi(
            string swaggerJson,
            IEnumerable<RouteOptions> routes,
            string serverOverride,
            SwaggerEndPointOptions endPointOptions)
        {
            var swagger = JObject.Parse(swaggerJson);

            if (swagger.ContainsKey("swagger"))
            {
                return TransformSwagger(swagger, routes, serverOverride, endPointOptions);
            }

            if (swagger.ContainsKey("openapi"))
            {
                return TransformOpenApi(swagger, routes, serverOverride, endPointOptions);
            }

            throw new InvalidOperationException("Unknown swagger/openapi version");
        }

        private string TransformSwagger(
            JObject swagger,
            IEnumerable<RouteOptions> routes,
            string hostOverride,
            SwaggerEndPointOptions endPointOptions)
        {
            JToken paths = swagger[SwaggerProperties.Paths];
            string basePath = swagger.ContainsKey(SwaggerProperties.BasePath)
                ? swagger.GetValue(SwaggerProperties.BasePath).ToString()
                : "";
            basePath = basePath.TrimEnd('/');

            RemoveHost(swagger);
            if (hostOverride != "")
            {
                AddHost(swagger, hostOverride);
            }

            if (paths != null)
            {
                RenameAndRemovePaths(routes, paths, basePath);

                if (endPointOptions.RemoveUnusedComponentsFromScheme)
                {
                    RemoveItems<JProperty>(
                        swagger[SwaggerProperties.Definitions],
                        paths,
                        i => $"$..[?(@*.$ref == '#/{SwaggerProperties.Definitions}/{i.Name}')]",
                        i => $"$..[?(@*.*.items.$ref == '#/{SwaggerProperties.Definitions}/{i.Name}')]",
                        i => $"$..[?(@*.*.allOf[?(@.$ref == '#/{SwaggerProperties.Definitions}/{i.Name}')])]",
                        i => $"$..allOf[?(@.$ref == '#/{SwaggerProperties.Definitions}/{i.Name}')]",
                        i => $"$..[?(@*.*.oneOf[?(@.$ref == '#/{SwaggerProperties.Definitions}/{i.Name}')])]");
                    if (swagger["tags"] != null)
                    {
                        RemoveItems<JObject>(
                            swagger[SwaggerProperties.Tags],
                            paths,
                            i => $"$..tags[?(@ == '{i[SwaggerProperties.TagName]}')]");
                    }
                }
            }

            if (swagger.ContainsKey(SwaggerProperties.BasePath))
            {
                swagger[SwaggerProperties.BasePath] = "/";
            }

            return swagger.ToString(Formatting.Indented);
        }

        private string TransformOpenApi(
            JObject openApi,
            IEnumerable<RouteOptions> routes,
            string serverOverride,
            SwaggerEndPointOptions endPointOptions)
        {
            // NOTE: Only supporting one server for now.
            string downstreamBasePath = "";
            if (openApi.GetValue(OpenApiProperties.Servers)?.Any() == true && !endPointOptions.TakeServersFromDownstreamService)
            {
                string firstServerUrl = openApi.GetValue(OpenApiProperties.Servers).First.Value<string>(OpenApiProperties.Url);
                var downstreamUrl = new Uri(firstServerUrl, UriKind.RelativeOrAbsolute);
                downstreamBasePath =
                    (downstreamUrl.IsAbsoluteUri ? downstreamUrl.AbsolutePath : downstreamUrl.OriginalString)
                    .RemoveSlashFromEnd();
            }

            JToken paths = openApi[OpenApiProperties.Paths];
            if (paths != null)
            {
                RenameAndRemovePaths(routes, paths, downstreamBasePath);

                JToken schemaToken = openApi[OpenApiProperties.Components][OpenApiProperties.Schemas];
                if (endPointOptions.RemoveUnusedComponentsFromScheme && schemaToken != null)
                {
                    RemoveItems<JProperty>(schemaToken,
                        paths,
                        i => $"$..[?(@*.$ref == '#/{OpenApiProperties.Components}/{OpenApiProperties.Schemas}/{i.Name}')]",
                        i => $"$..[?(@*.*.items.$ref == '#/{OpenApiProperties.Components}/{OpenApiProperties.Schemas}/{i.Name}')]",
                        i => $"$..[?(@*.*.allOf[?(@.$ref == '#/{OpenApiProperties.Components}/{OpenApiProperties.Schemas}/{i.Name}')])]",
                        i => $"$..allOf[?(@.$ref == '#/{OpenApiProperties.Components}/{OpenApiProperties.Schemas}/{i.Name}')]",
                        i => $"$..oneOf[?(@.$ref == '#/{OpenApiProperties.Components}/{OpenApiProperties.Schemas}/{i.Name}')]",
                        i => $"$..anyOf[?(@.$ref == '#/{OpenApiProperties.Components}/{OpenApiProperties.Schemas}/{i.Name}')]",
                        i => $"$..[?(@*.*.oneOf[?(@.$ref == '#/{OpenApiProperties.Components}/{OpenApiProperties.Schemas}/{i.Name}')])]");
                }

                if (endPointOptions.RemoveUnusedComponentsFromScheme && openApi["tags"] != null)
                {
                    RemoveItems<JObject>(
                        openApi[OpenApiProperties.Tags],
                        paths,
                        i => $"$..tags[?(@ == '{i[OpenApiProperties.TagName]}')]");
                }
            }

            TransformServerPaths(openApi, serverOverride, endPointOptions.TakeServersFromDownstreamService);

            return openApi.ToString(Formatting.Indented);
        }

        private void RenameAndRemovePaths(IEnumerable<RouteOptions> routes, JToken paths, string basePath)
        {
            var forRemove = new List<JProperty>();

            for (int i = 0; i < paths.Count(); i++)
            {
                var path = paths.ElementAt(i) as JProperty;
                string downstreamPath = path.Name.RemoveSlashFromEnd();
                RouteOptions route = FindRoute(routes, path.Name.WithShashEnding(), basePath);

                if (route != null && RemoveMethods(path, route))
                {
                    AddSecurityDefinitions(path, route);

                    RenameToken(path, ConvertDownstreamPathToUpstreamPath(downstreamPath, route.DownstreamPath, route.UpstreamPath, basePath));
                }
                else
                {
                    forRemove.Add(path);
                }
            }

            foreach (JProperty p in forRemove)
            {
                p.Remove();
            }
        }

        private bool RemoveMethods(JProperty path, RouteOptions route)
        {
            var forRemove = new List<JProperty>();
            var method = path.First.First as JProperty;

            while (method != null)
            {
                if (!route.ContainsHttpMethod(method.Name))
                {
                    forRemove.Add(method);
                }
                method = method.Next as JProperty;
            }

            foreach (JProperty m in forRemove)
            {
                m.Remove();
            }

            return path.First.Any();
        }

        private void AddSecurityDefinitions(JProperty path, RouteOptions route)
        {
            var authProviderKey = route.AuthenticationOptions?.AuthenticationProviderKey;

            if (string.IsNullOrEmpty(authProviderKey))
            {
                return;
            }

            if (_ocelotSwaggerGenOptions.AuthenticationProviderKeyMap.TryGetValue(
                authProviderKey,
                out var securityScheme))
            {
                var method = path.First.First as JProperty;

                while (method != null)
                {
                    var securityProperty = new JProperty(OpenApiProperties.Security,
                        new JArray(
                            new JObject(
                                new JProperty(securityScheme,
                                    new JArray(route.AuthenticationOptions?.AllowedScopes?.ToArray() ?? Array.Empty<string>())))));

                    ((JObject)method.Value).Add(securityProperty);

                    method = method.Next as JProperty;
                }
            }
        }

        private static void RemoveItems<T>(JToken token, JToken paths, params Func<T, string>[] searchPaths)
            where T : class
        {
            var forRemove = token
                .Cast<T>()
                .AsParallel()
                .Where(
                    i => searchPaths.Select(p => paths.SelectTokens(p(i)).Any())
                        .All(p => !p))
                .ToHashSet();

            if (typeof(T) == typeof(JProperty))
            {
                CheckSubreferences(token, searchPaths, forRemove);
            }

            foreach (T item in forRemove)
            {
                if (item is JObject o)
                {
                    o.Remove();
                }
                else if (item is JProperty t)
                {
                    t.Remove();
                }
            }
        }

        private static void CheckSubreferences<T>(IEnumerable<JToken> token, Func<T, string>[] searchPaths, HashSet<T> forRemove)
            where T : class
        {
            var notForRemove = token.Cast<T>().Where(t => !forRemove.Contains(t)).Cast<JProperty>().ToList();
            var subReference = forRemove
                    .Cast<JProperty>()
                    .Where(i
                    => searchPaths
                        .Select(p => notForRemove.Any(t => t.SelectTokens(p(i as T)).Any())).Any(p => p))
                    .ToDictionary(p => p.Name, p => p);

            forRemove.RemoveWhere(p => subReference.ContainsKey((p as JProperty).Name));

            if (subReference.Count > 0)
            {
                CheckSubreferences(subReference.Values, searchPaths, forRemove);
            }
        }

        private static RouteOptions FindRoute(IEnumerable<RouteOptions> routes, string downstreamPath, string basePath)
        {
            string downstreamPathWithBasePath = PathHelper.BuildPath(basePath, downstreamPath);
            return routes.FirstOrDefault(p
                => p.CanCatchAll
                    ? downstreamPathWithBasePath.StartsWith(p.DownstreamPathWithSlash, StringComparison.CurrentCultureIgnoreCase)
                    : p.DownstreamPathWithSlash.Equals(downstreamPathWithBasePath, StringComparison.CurrentCultureIgnoreCase));
        }

        private static void AddHost(JObject swagger, string swaggerHost)
        {
            swaggerHost = swaggerHost.Contains(Uri.SchemeDelimiter) ? new Uri(swaggerHost).Authority : swaggerHost;
            swagger.Add(SwaggerProperties.Host, swaggerHost);
        }

        private static void RemoveHost(JObject swagger)
        {
            swagger.Remove(SwaggerProperties.Host);
            swagger.Remove(SwaggerProperties.Schemes);
        }

        private static string ConvertDownstreamPathToUpstreamPath(string downstreamPath, string downstreamPattern, string upstreamPattern, string downstreamBasePath)
        {
            if (downstreamBasePath.Length > 0)
            {
                downstreamPath = PathHelper.BuildPath(downstreamBasePath, downstreamPath);
            }

            int pos = downstreamPath.IndexOf(downstreamPattern, StringComparison.CurrentCultureIgnoreCase);
            if (pos < 0)
            {
                return downstreamPath;
            }
            return $"{downstreamPath.Substring(0, pos)}{upstreamPattern}{downstreamPath.Substring(pos + downstreamPattern.Length)}";
        }

        private static void RenameToken(JProperty property, string newName)
        {
            var newProperty = new JProperty(newName, property.Value);
            property.Replace(newProperty);
        }

        private static void TransformServerPaths(JObject openApi, string serverOverride, bool takeServersFromDownstreamService)
        {
            if (!openApi.ContainsKey(OpenApiProperties.Servers) || takeServersFromDownstreamService)
            {
                return;
            }

            foreach (JToken server in openApi.GetValue(OpenApiProperties.Servers))
            {
                if (server[OpenApiProperties.Url] != null)
                {
                    server[OpenApiProperties.Url] = serverOverride.RemoveSlashFromEnd();
                }
            }
        }

        private static string ComputeHash(string input)
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            byte[] hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }
    }
}
