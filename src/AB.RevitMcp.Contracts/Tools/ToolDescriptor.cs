using System;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    /// <summary>Risk classification. Drives MCP annotations and the add-in's confirmation gate.</summary>
    public enum ToolCategory
    {
        /// <summary>Pure query. Never opens a Revit transaction.</summary>
        Read = 0,

        /// <summary>Creates or edits model data inside a transaction group.</summary>
        Write = 1,

        /// <summary>Removes or unloads data. Requires an explicit <c>confirm: true</c> argument.</summary>
        Destructive = 2
    }

    /// <summary>
    /// One MCP tool: its name, human description, JSON Schema for arguments, and risk category.
    /// The catalog lives in the shared contracts assembly so the MCP server can advertise tools
    /// even while Revit is closed, and the add-in binds handlers to the very same descriptors.
    /// </summary>
    public sealed class ToolDescriptor
    {
        public string Name { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public ToolCategory Category { get; private set; }
        public JsonValue InputSchema { get; private set; }

        public ToolDescriptor(string name, ToolCategory category, string title, string description, JsonValue inputSchema)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Tool name is required.", "name");
            Name = name;
            Category = category;
            Title = title ?? name;
            Description = description ?? string.Empty;
            InputSchema = inputSchema ?? Sch.NoArgs();
        }

        public bool IsDestructive { get { return Category == ToolCategory.Destructive; } }
        public bool IsReadOnly { get { return Category == ToolCategory.Read; } }

        /// <summary>Serializes to the shape required by MCP <c>tools/list</c>.</summary>
        public JsonValue ToMcpTool()
        {
            JsonValue t = JsonValue.NewObject();
            t.Set("name", Name);
            t.Set("title", Title);

            string desc = Description;
            if (Category == ToolCategory.Destructive)
                desc += " DESTRUCTIVE: this permanently changes the model and requires \"confirm\": true.";
            else if (Category == ToolCategory.Write)
                desc += " Modifies the open Revit model (undoable from Revit's undo stack).";
            t.Set("description", desc);

            t.Set("inputSchema", InputSchema.Clone());

            JsonValue ann = JsonValue.NewObject();
            ann.Set("title", Title);
            ann.Set("readOnlyHint", Category == ToolCategory.Read);
            ann.Set("destructiveHint", Category == ToolCategory.Destructive);
            ann.Set("idempotentHint", Category == ToolCategory.Read);
            ann.Set("openWorldHint", false);
            t.Set("annotations", ann);

            return t;
        }

        /// <summary>Serializes for the bridge's own <c>bridge/describe</c> reply (diagnostics).</summary>
        public JsonValue ToDescriptorJson()
        {
            JsonValue t = JsonValue.NewObject();
            t.Set("name", Name);
            t.Set("title", Title);
            t.Set("category", Category.ToString().ToLowerInvariant());
            t.Set("description", Description);
            t.Set("inputSchema", InputSchema.Clone());
            return t;
        }
    }
}
