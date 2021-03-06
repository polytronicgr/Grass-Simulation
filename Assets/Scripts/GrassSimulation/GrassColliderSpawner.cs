﻿using UnityEngine;

public class GrassColliderSpawner : MonoBehaviour
{
	public GameObject GrassCollider;
	public KeyCode SpawnKey = KeyCode.F;
	[Range(0, 10)]
	public float SpawnInterval;
	[Range(0, 10)]
	public float ScaleRandomness;
	[Range(0.01f, 10)]
	public float MinScale = 0.5f;
	[Range(0, 10)]
	public float MassRandomness;
	[Range(0, 100)]
	public float Lifetime;
	[Range(0, 100)]
	public float VelocityModifier;
	private float _spawnTimer;
	
	// Use this for initialization
	private void Start() { }

	// Update is called once per frame
	private void Update()
	{
		if (!GrassCollider) return;
		_spawnTimer += Time.deltaTime;
		if (Input.GetKey(SpawnKey) && _spawnTimer > SpawnInterval)
		{
			_spawnTimer = 0;
			var grassCollider = Instantiate(GrassCollider, transform.position, transform.rotation);
			var rigidBody = grassCollider.GetComponent<Rigidbody>();
			rigidBody.velocity = transform.TransformDirection(Vector3.forward * VelocityModifier);
			rigidBody.transform.localScale = Random.Range(MinScale, MinScale + ScaleRandomness) * grassCollider.transform.localScale;
			rigidBody.mass = Random.Range(0.01f, 0.5f + MassRandomness);
			Destroy(grassCollider, Lifetime);
		}
	}
}