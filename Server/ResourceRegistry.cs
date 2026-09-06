using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
{
    public class ResourceRegistry
    {
        private readonly Dictionary<string, RegisteredResource> _resources = new Dictionary<string, RegisteredResource>();
        private readonly EngineConnection _engine;
        private readonly JsonSerializerOptions _jsonOptions;

        public ResourceRegistry(EngineConnection engine)
        {
            _engine = engine;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
            RegisterBuiltinResources();
        }

        public ResourceListResult ListResources()
        {
            var resources = new List<ResourceDefinition>();
            foreach (var entry in _resources.Values)
                resources.Add(entry.Definition);
            return new ResourceListResult { Resources = resources };
        }

        public async Task<ResourceReadResult> ReadResource(JsonElement paramsElement, CancellationToken ct)
        {
            if (!paramsElement.TryGetProperty("uri", out var uriElement))
                throw new InvalidOperationException("Missing 'uri' in resource read request");

            var uri = uriElement.GetString();
            if (uri == null || !_resources.TryGetValue(uri, out var resource))
                throw new InvalidOperationException($"Unknown resource: {uri}");

            var content = await resource.Handler(ct).ConfigureAwait(false);

            return new ResourceReadResult
            {
                Contents = new List<ResourceContent>
                {
                    new ResourceContent
                    {
                        Uri = uri,
                        MimeType = resource.Definition.MimeType ?? "text/plain",
                        Text = content
                    }
                }
            };
        }

        private void RegisterBuiltinResources()
        {
            Register("scene://hierarchy", "Scene Hierarchy",
                "Current scene tree structure",
                "text/plain",
                ct => _engine.ForwardResourceRead("get_scene_hierarchy", ct));

            Register("project://info", "Project Info",
                "Engine and project information",
                "text/plain",
                ct => _engine.ForwardResourceRead("get_project_info", ct));

            Register("editor://logs", "Recent Logs",
                "Recent editor/engine log entries",
                "text/plain",
                ct => _engine.ForwardResourceRead("get_recent_logs", ct));

            Register("engine://types", "Available Types",
                "List of registered engine types",
                "text/plain",
                ct => _engine.ForwardResourceRead("get_engine_types", ct));

            Register("editor://compile_status", "Compilation Status",
                "Current script compilation status with errors and warnings",
                "text/plain",
                ct => _engine.ForwardResourceRead("get_compile_status", ct));

            // Served by the server itself: readable before any engine connects.
            Register(ServerInstructions.GuideUri, "AkerMCP guide",
                "How to drive the engine well: inspect, modify, verify; property paths; " +
                "execute rules; screenshots; recovering from a modal dialog.",
                "text/markdown",
                _ => Task.FromResult(ServerInstructions.Guide));
        }

        private void Register(string uri, string name, string description,
            string mimeType, Func<CancellationToken, Task<string>> handler)
        {
            _resources[uri] = new RegisteredResource
            {
                Definition = new ResourceDefinition
                {
                    Uri = uri,
                    Name = name,
                    Description = description,
                    MimeType = mimeType
                },
                Handler = handler
            };
        }

        private class RegisteredResource
        {
            public ResourceDefinition Definition { get; set; } = null!;
            public Func<CancellationToken, Task<string>> Handler { get; set; } = null!;
        }
    }
}
