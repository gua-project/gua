using System;
using System.Collections.Generic;
using Gua.Core;
using UnityEngine;

namespace Gua.Unity
{
[DisallowMultipleComponent]
public sealed class GuaWorldObject : MonoBehaviour
{
    [SerializeField] private string id = string.Empty;
    [SerializeField] private string kind = "object";
    [SerializeField] private string label = string.Empty;
    [SerializeField, TextArea] private string description = string.Empty;
    [SerializeField] private GuaWorldSpace space = GuaWorldSpace.World3D;
    [SerializeField] private bool visibleToPlayer;
    [SerializeField] private bool active = true;
    [SerializeField] private GuaAgentExposure agentExposure;
    [SerializeField] private string[] tags = Array.Empty<string>();
    [SerializeField] private string domainId = string.Empty;
    [SerializeField] private string relatedUiNodeId = string.Empty;
    private readonly Dictionary<string, object?> state = new(StringComparer.Ordinal);
    private GuaObserveOwner? observeOwner;
    private readonly List<GuaObserveRegistration> observations = new();

    public string Id { get => id; set { if (id != value) DisposeObservations(); id = value ?? string.Empty; } }
    /// <summary>Call after the first successful World frame. worldPosition is
    /// already reflected; register only game-specific state such as phase.</summary>
    public GuaObserveRegistration Observe(string name, Func<GuaValue> getter, bool allowPlayer = false, bool sensitive = false)
    {
        observeOwner ??= GuaUnityRuntime.CreateObserveOwner(GuaObserveSource.Object, Id);
        var registration = observeOwner.Observe(name, getter, allowPlayer, sensitive);
        observations.Add(registration); return registration;
    }
    public void DisposeObservations()
    {
        foreach (var observation in observations) observation.Dispose();
        observations.Clear(); observeOwner?.Dispose(); observeOwner = null;
    }
    private void OnDestroy() => DisposeObservations();
    public string Kind { get => kind; set => kind = value ?? string.Empty; }
    public string Label { get => string.IsNullOrEmpty(label) ? gameObject.name : label; set => label = value ?? string.Empty; }
    public string Description { get => description; set => description = value ?? string.Empty; }
    public GuaWorldSpace Space { get => space; set => space = value; }
    public bool VisibleToPlayer { get => visibleToPlayer; set => visibleToPlayer = value; }
    public bool Active { get => active; set => active = value; }
    public GuaAgentExposure AgentExposure { get => agentExposure; set => agentExposure = value; }
    public IReadOnlyList<string> Tags { get => tags; set => tags = value == null ? Array.Empty<string>() : new List<string>(value).ToArray(); }
    public IReadOnlyDictionary<string, object?> State => state;
    public string DomainId { get => domainId; set => domainId = value ?? string.Empty; }
    public string RelatedUiNodeId { get => relatedUiNodeId; set => relatedUiNodeId = value ?? string.Empty; }
    public void SetState(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("State key is required.", nameof(key));
        if (value is not null and not string and not bool and not byte and not sbyte and not short and not ushort and not int and not uint and not long and not ulong and not float and not double and not decimal)
            throw new ArgumentException("World state values must be primitive strings, numbers, booleans, or null.", nameof(value));
        state[key] = value;
    }
    public bool RemoveState(string key) => state.Remove(key);
}
}
