using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "HoldLibrary", menuName = "Climbing/Hold Library")]
public class HoldLibrary : ScriptableObject
{
    public List<HoldDefinition> holds = new List<HoldDefinition>();
}

[System.Serializable]
public class HoldDefinition
{
    public string name = "Jug";
    public GameObject prefab;
    [Tooltip("Scale multiplier — Jug=1.0, Crimp=0.5")]
    public float scale = 1f;
    public Color color = Color.white;
}