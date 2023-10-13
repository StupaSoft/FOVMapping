using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RaycastFOVManager : MonoBehaviour
{
	[SerializeField] private float sightRadius = 10.0f;

	[SerializeField]
	[Range(0.0f, 360.0f)]
	private float sightAngle = 120.0f;

	[SerializeField] private LayerMask obstacleMask;

	[SerializeField] private float rayCountPerDegree = 0.3f;

	private MeshFilter sightMeshFilter;
	private Mesh sightMesh;

	[SerializeField]
	[Range(0, 10)]
	private int edgeIterationCount = 4;

	[SerializeField]
	[Range(0.0f, 100.0f)]
	private float castThreshold = 1.0f;

	[SerializeField] private float interval = 0.02f;

	private struct ViewCastInfo
	{
		public bool isHit { get; set; }
		public Vector3 point { get; set; }
		public float distance { get; set; }
		public float angle { get; set; }
	}

	private struct EdgeInfo
	{
		public Vector3 pointA { get; set; }
		public Vector3 pointB { get; set; }
	}

	private void Start()
	{
		sightMeshFilter = GetComponent<MeshFilter>();
		sightMesh = new Mesh();
		sightMesh.name = "Sight Mesh";
		sightMeshFilter.mesh = sightMesh;

		StartCoroutine(DrawFieldOfView());
	}

	private IEnumerator DrawFieldOfView()
	{
		while (true)
		{
			yield return new WaitForSeconds(interval);

			int rayCount = Mathf.RoundToInt(sightAngle * rayCountPerDegree);
			float angleBetweenRays = sightAngle / rayCount;
			List<Vector3> viewPoints = new List<Vector3>();
			ViewCastInfo prevViewCastInfo = new ViewCastInfo();

			for (int i = 0; i < rayCount + 1; ++i)
			{
				float angle = transform.eulerAngles.y - sightAngle / 2.0f + angleBetweenRays * i;
				ViewCastInfo currViewCastInfo = ViewCast(angle);

				if (i > 0)
				{
					if (prevViewCastInfo.isHit != currViewCastInfo.isHit)
					{
						EdgeInfo edge = FindEdge(prevViewCastInfo, currViewCastInfo);
						if (edge.pointA != Vector3.zero)
						{
							viewPoints.Add(edge.pointA);
						}
						if (edge.pointB != Vector3.zero)
						{
							viewPoints.Add(edge.pointB);
						}
					}

					if (prevViewCastInfo.isHit && currViewCastInfo.isHit && Mathf.Abs(currViewCastInfo.distance - prevViewCastInfo.distance) >= castThreshold)
					{
						EdgeInfo edge = FindEdge(prevViewCastInfo, currViewCastInfo);
						if (edge.pointA != Vector3.zero)
						{
							viewPoints.Add(edge.pointA);
						}
						if (edge.pointB != Vector3.zero)
						{
							viewPoints.Add(edge.pointB);
						}
					}
				}

				viewPoints.Add(currViewCastInfo.point);
				prevViewCastInfo = currViewCastInfo;
			}

			int vertexCount = viewPoints.Count + 1; // The origin vertex
			Vector3[] vertices = new Vector3[vertexCount];
			int[] triangles = new int[(vertexCount - 2) * 3];

			vertices[0] = Vector3.zero;
			for (int i = 0; i < vertexCount - 1; ++i)
			{
				vertices[i + 1] = transform.InverseTransformPoint(viewPoints[i]);

				if (i < vertexCount - 2)
				{
					triangles[i * 3] = 0;
					triangles[i * 3 + 1] = i + 1;
					triangles[i * 3 + 2] = i + 2;
				}
			}

			sightMesh.Clear();
			sightMesh.vertices = vertices;
			sightMesh.triangles = triangles;
			sightMesh.RecalculateNormals();
		}
	}

	private ViewCastInfo ViewCast(float angle)
	{
		Vector3 direction = DirectionFromAngle(angle);
		RaycastHit hit;
		if (Physics.Raycast(transform.position, direction, out hit, sightRadius, obstacleMask))
		{
			return new ViewCastInfo 
			{ 
				isHit = true, 
				point = hit.point,
				distance = hit.distance, 
				angle = angle 
			};
		}
		else
		{
			return new ViewCastInfo
			{
				isHit = false,
				point = transform.position + sightRadius * direction,
				distance = sightRadius,
				angle = angle
			};
		}
	}

	private EdgeInfo FindEdge(ViewCastInfo minViewCastInfo, ViewCastInfo maxViewCastInfo)
	{
		float minAngle = minViewCastInfo.angle;
		float maxAngle = maxViewCastInfo.angle;
		Vector3 minPoint = Vector3.zero;
		Vector3 maxPoint = Vector3.zero;

		for (int i = 0; i < edgeIterationCount; ++i)
		{
			float angle = (minAngle + maxAngle) / 2.0f;
			ViewCastInfo currViewCastInfo = ViewCast(angle);
			if (currViewCastInfo.isHit == minViewCastInfo.isHit && Mathf.Abs(minViewCastInfo.distance - currViewCastInfo.distance) < castThreshold)
			{
				minAngle = angle;
				minPoint = currViewCastInfo.point;
			}
			else
			{
				maxAngle = angle;
				maxPoint = currViewCastInfo.point;
			}
		}

		return new EdgeInfo
		{ 
			pointA = minPoint,
			pointB = maxPoint
		};

	}

	private Vector3 DirectionFromAngle(float angle)
	{
		return new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad), 0.0f, Mathf.Cos(angle * Mathf.Deg2Rad));
	}
}
