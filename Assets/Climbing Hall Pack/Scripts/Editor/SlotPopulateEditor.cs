using UnityEngine;
using System.Collections;
using UnityEditor;

[CustomEditor(typeof(SlotPopulator))]
public class SlotPopulateEditor : Editor
{

	public override void OnInspectorGUI ()
	{
		DrawDefaultInspector ();

		SlotPopulator myTarget = (SlotPopulator)target;
		if (GUILayout.Button ("Populate")) 
		{		
			myTarget.PopulateHolds ();
		}
	}
}
