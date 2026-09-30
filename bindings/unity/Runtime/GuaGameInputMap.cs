using System;
using System.Collections.Generic;
using System.Linq;
using Gua.Core;
using Gua.Runtime;
using UnityEngine;

namespace Gua.Unity
{

[Serializable]
public sealed class GuaGameInputAction
{
    public string Id = "";
    [TextArea] public string Description = "";
    public GuaGameInputValueType ValueType = GuaGameInputValueType.Button;
    public bool HasRange;
    public float Minimum = -1;
    public float Maximum = 1;
    public bool Holdable;
    public bool Active = true;
    public string[] Bindings = Array.Empty<string>();
    public string Risk = "safe";
    public bool RequiresConfirmation;
    public string Category = "";
    public string[] Aliases = Array.Empty<string>();
    public string[] Tags = Array.Empty<string>();
    public GuaAgentExposure AgentExposure;
    [TextArea] public string ValueSchemaJson = "";
    [TextArea] public string ExamplesJson = "";

    internal GuaGameInputActionDescriptor Descriptor() => new(
        Id, Description, ValueType, HasRange ? Minimum : null, HasRange ? Maximum : null,
        Holdable, Active, Bindings, Risk, RequiresConfirmation, Category, Aliases, Tags, AgentExposure) { ValueSchemaJson = string.IsNullOrEmpty(ValueSchemaJson) ? null : ValueSchemaJson,
            ExamplesJson = string.IsNullOrEmpty(ExamplesJson) ? null : ExamplesJson };
}

public sealed class GuaGameInputMap : MonoBehaviour
{
    public string Context = "gameplay";
    public bool EnableRawInput;
    [Tooltip("Allow Player/Public Agent transports such as WebMCP to invoke registered semantic actions.")]
    public bool AllowPlayerAgentSemanticInput;
    [Tooltip("Allow Player/Public Agent transports such as WebMCP to inject raw keyboard, pointer, gamepad, and text input.")]
    public bool AllowPlayerAgentRawInput;
    public List<GuaGameInputAction> Actions = new();
    internal IReadOnlyList<GuaGameInputActionDescriptor> Descriptors() => Actions.Select(action => action.Descriptor()).ToArray();
}

}
