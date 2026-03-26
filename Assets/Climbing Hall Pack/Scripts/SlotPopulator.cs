using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class SlotPopulator : MonoBehaviour
{
	public float RaycastLength = 1f;

	List<Vector3> Directions = new List<Vector3> () 
	{
		-Vector3.up,
		Vector3.up,
		-Vector3.right,
		Vector3.right,
		-Vector3.forward,
		Vector3.forward
	};




	/*public enum SurfaceType
	{
		ThreeDimensional,
		Flat_Z,
		Flat_Y,
		Flat_X,
	}

	public SurfaceType Surface;
	public bool Mirror = false;*/


	public GameObject Wall;
	public List<Transform> Slots = new List<Transform>();
	public List<GameObject> HoldPrefabs = new List<GameObject>();
	public List<Material> HoldColorVariations = new List<Material> ();

	public bool DEBUG_MODE = false;

	[HideInInspector]public List<GameObject> Holds = new List<GameObject>();



	public void PopulateHolds()
	{
		if (DEBUG_MODE)
			Debug.Log ("Populate started");

		if (Wall.GetComponent<Collider> () == null)
			Wall.AddComponent<MeshCollider> ();

		foreach (var h in Holds)
			DestroyImmediate (h);
		Holds.Clear ();

		foreach (var slot in Slots) 
		{		
			if (DEBUG_MODE) Debug.Log ("Find normal of " + slot.name);

			float closestSurfaceDistance = Mathf.Infinity;
			Vector3 normal = Vector3.zero;

			foreach (var dir in Directions) 
			{ 
				var start = slot.position - dir * RaycastLength/2;
				RaycastHit hit;
				if (Physics.Raycast (start, dir, out hit, RaycastLength))
				{					
					float distance = Vector3.Distance (slot.position, hit.point);
					if (distance < closestSurfaceDistance) 
					{
						normal = hit.normal;
						closestSurfaceDistance = distance;

						if (DEBUG_MODE)
							Debug.DrawLine (start, start + dir * RaycastLength, Color.blue, 10f);
					}
				}
				else if (DEBUG_MODE)
				{
					Debug.LogWarning ("Can't find a surface, please increse the raycast length.");
					Debug.DrawLine (start, start + dir * RaycastLength, Color.red, 10f);
				}
			}


			// PLACE HOLD
			if(closestSurfaceDistance != Mathf.Infinity)
			{
				// Get random hold
				var randomHoldPrefab = HoldPrefabs [Random.Range (0, HoldPrefabs.Count)];

				if (DEBUG_MODE)
					Debug.Log ("Create hand (" + randomHoldPrefab.name + ") on slot (" + slot.name + ")");

				// Instantiate hold
				var go = (GameObject)Instantiate (randomHoldPrefab, slot.position, slot.rotation);
				go.transform.SetParent (slot);
				go.transform.up = normal;

				Holds.Add (go);

				if (HoldColorVariations.Count > 0) {
					int index = Random.Range (-1, HoldColorVariations.Count);
					if (index > -1) {
						var mesh = go.GetComponent<MeshRenderer> ();
						mesh.material = HoldColorVariations [index];
					}
				}
			}
			else if (DEBUG_MODE)
				Debug.Log ("Surface didn't found for slot (" + slot.name + ")");
			

		}	








		/*Vector3 direction = Vector3.zero;
		switch (Surface) 
		{
		case SurfaceType.Flat_X:
			direction = Vector3.right;
			break;
		case SurfaceType.Flat_Y:
			direction = Vector3.up;
			break;
		case SurfaceType.Flat_Z:
			direction = Vector3.forward;
			break;
		}

		if (Mirror)
			direction *= -1;

		foreach (var slot in Slots) 
		{		
			if(direction == Vector3.zero)
				direction = (slot.position - transform.position).normalized;
			
			var start = slot.position + direction;

			if (DEBUG_MODE) 
				Debug.DrawLine (start, start - direction * 1, Color.red, 10f);

			// Get wall face normal
			RaycastHit hit;
			if (Physics.Raycast (start, -direction, out hit)) {
				var normal = hit.normal;

				// Get random hold
				var randomHoldPrefab = HoldPrefabs [Random.Range (0, HoldPrefabs.Count)];

				if (DEBUG_MODE)
					Debug.Log ("Create hand (" + randomHoldPrefab.name + ") on slot (" + slot.name + ")");

				// Instantiate hold
				var go = (GameObject)Instantiate (randomHoldPrefab, slot.position, slot.rotation);
				go.transform.SetParent (slot);
				go.transform.up = normal;

				Holds.Add (go);

				if (HoldColorVariations.Count > 0) {
					int index = Random.Range (-1, HoldColorVariations.Count);
					if (index > -1) {
						var mesh = go.GetComponent<MeshRenderer> ();
						mesh.material = HoldColorVariations [index];
					}
				}
			} else if (DEBUG_MODE)
				Debug.LogWarning ("Raycast didn't hit anything (from "+start+" to " + direction+" for slot (" + slot.name + ")");
		}	*/		
	}
}
