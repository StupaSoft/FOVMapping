using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
public class PlayerController : MonoBehaviour
{
	private CharacterController characterController;
	[SerializeField] private float speed = 20.0f;
	[SerializeField] private float rotation = 3.0f;

	private void Awake()
	{
		characterController = GetComponent<CharacterController>();
	}

	private void FixedUpdate()
	{
		// Movement
		Vector3 direction = Vector3.zero;
		if (Input.GetKey(KeyCode.W))
		{
			direction = transform.forward;
		}
		else if (Input.GetKey(KeyCode.S)) 
		{
			direction = -transform.forward;
		}
		characterController.SimpleMove(direction * speed * Time.fixedDeltaTime);

		// Rotation
		float angularDirection = 0.0f;
		if (Input.GetKey(KeyCode.A))
		{
			angularDirection = -rotation;
		}
		else if (Input.GetKey(KeyCode.D)) 
		{
			angularDirection = rotation;
		}
		transform.Rotate(Vector3.up * angularDirection * rotation * Time.fixedDeltaTime);
	}
}
}