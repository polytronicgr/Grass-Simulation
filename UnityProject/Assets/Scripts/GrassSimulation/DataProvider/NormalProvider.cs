﻿using System;
using UnityEngine;

namespace GrassSimulation.DataProvider
{
	[Serializable]
	public abstract class NormalProvider : MonoBehaviour
	{
		/// <summary>
		///   <para>Gets the normal at point x,y.</para>
		/// </summary>
		/// <returns>A normal as Vector3</returns>
		/// <param name="x">x coordinate in range 0..1</param>
		/// <param name="y">y coordnate in range 0..1</param>
		public abstract Vector3 GetNormal(float x, float y);
	}
}