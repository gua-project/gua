using UnityEngine;

public sealed class SpatialGeometryHost : MonoBehaviour
{
    void Start() { SpatialFixture.RunCore(); }
}
