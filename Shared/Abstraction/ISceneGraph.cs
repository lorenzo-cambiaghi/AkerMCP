using System.Collections.Generic;

namespace MCPSharp.Shared.Abstraction
{
    public interface ISceneGraph
    {
        ISceneNode? GetNode(string path);
        IEnumerable<ISceneNode> GetRootNodes();
        IEnumerable<ISceneNode> Query(QueryFilter filter);
        ISceneNode CreateNode(string type, string? name, string? parentPath);
        bool DeleteNode(string path, bool recursive = true);
        int GetTotalNodeCount();
    }

    public interface ISceneNode
    {
        string Name { get; }
        string Path { get; }
        string TypeName { get; }
        ISceneNode? Parent { get; }
        IEnumerable<ISceneNode> Children { get; }

        object? GetProperty(string propertyPath);
        void SetProperty(string propertyPath, object? value);
        object? CallMethod(string methodName, object?[]? args);

        IEnumerable<PropertyDescriptor> GetProperties();
        IEnumerable<MethodDescriptor> GetMethods();

        object UnderlyingObject { get; }
    }
}
