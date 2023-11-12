using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
// Attach this component to 'eyes' of the field of view.
// Works only when this component is enabled.
public class FOVAgent : MonoBehaviour
{
	[Tooltip("Is this agent an eye(set to true for friendly agents and false for hostile agents)?")]
	[SerializeField]
	private bool _contributeToFOV = true;
	public bool contributeToFOV { get => _contributeToFOV; set => _contributeToFOV = value; }

	[Tooltip("How far can an agent see? This value must be equal to or less than the samplingRange of a generated FOV map.")]
	[SerializeField]
	[Range(0.0f, 1000.0f)]
	private float _sightRange = 50.0f;
	public float sightRange { get => _sightRange; set => _sightRange = value; }

	[Tooltip("How widely can an agent see?")]
	[SerializeField]
	[Range(0.0f, 360.0f)]
	private float _sightAngle = 240.0f;
	public float sightAngle { get => _sightAngle; set => _sightAngle = value; }

	[Tooltip("Will this agent disappear if it is in a fog of war(set to true for hostile agents and false for friendly agents)?")]
	[SerializeField]
	private bool _disappearInFOW = false;
	public bool disappearInFOW { get => _disappearInFOW; set => _disappearInFOW = value; }

	[Tooltip("On the boundary of a field of view, if an agent with `disappearInFOW` set to true is under a fog of war whose opacity is larger than this value, the agent disappears.")]
	[SerializeField]
	[Range(0.0f, 1.0f)]
	private float _disappearAlphaThreshold = 0.1f;
	public float disappearAlphaThreshold { get => _disappearAlphaThreshold; set => _disappearAlphaThreshold = value; }
	private bool isUnderFOW = false;
	private MeshRenderer meshRenderer;

	private void Awake()
	{
		meshRenderer = GetComponent<MeshRenderer>();
	}

	[HideInInspector]
	public void SetUnderFOW(bool isUnder)
	{
		isUnderFOW = isUnder;
		if (disappearInFOW)
		{
			if (meshRenderer == null) return;
			meshRenderer.enabled = isUnder;
		}
	}

	public bool IsUnderFOW()
	{
		return isUnderFOW;
	}
}
}