﻿using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Timeline;
using Bounds = GrassSimulation.Utils.Bounds;

namespace GrassSimulation.LOD
{
	/**
	 * Info on GrassBlades:
	 * 	X,Z Coordinates ]0.0, 1.0[
	 * 	 - relative to the patch
	 * 	 - Applying _patchModelMatrix:
	 * 	 	 - translates to lower corner of bounding box
	 * 	 	 - scales to PatchSize
	 * 
	 * Y Coordinate ]0.0, 1.0[
	 * 	 - is the sampled height of the terrains heightmap
	 * 	 - Applying _patchModelMatrix:
	 * 		 - translates to Transform.position.y
	 * 		 - scales to TerrainSize
	 */
	public class GrassPatch : Patch, IDestroyable, IDrawable
	{
		private readonly uint[] _argsBillboardCrossed = {0, 0, 0, 0, 0};
		private readonly ComputeBuffer _argsBillboardCrossedBuffer;
		private readonly uint[] _argsBillboardScreen = {0, 0, 0, 0, 0};
		private readonly ComputeBuffer _argsBillboardScreenBuffer;
		private readonly uint[] _argsGeometry = {0, 0, 0, 0, 0};
		private readonly ComputeBuffer _argsGeometryBuffer;
		private readonly Bounds.BoundsVertices _boundsVertices;
		private readonly MaterialPropertyBlock _materialPropertyBlock;
		private readonly Vector4 _patchTexCoord; //x: xStart, y: yStart, z: width, w:height
		private readonly int _startIndex;
		private readonly float _parameterOffsetX;
		private readonly float _parameterOffsetY;
		private bool _applyTransition;
		private Mesh _dummyMesh;

		public Texture2D _normalHeightTexture;
		public RenderTexture _simulationTexture;

		/*
		 * _patchModelMatrix Notes:
		 * 		Translation
		 * 			X: bounds.center.x - bounds.extents.x
		 * 			Y: Context.Transform.position.y
		 * 			Z: bounds.center.z - bounds.extents.z
		 * 		Rotation
		 * 			None as Unity Terrain doesn't take rotation into account either
		 * 		Scale
		 * 			X: PatchSize
		 * 			Y: TerrainHeight
		 * 			Z: PatchSize
		 */
		private Matrix4x4 _patchModelMatrix;

		public GrassPatch(SimulationContext ctx, Vector4 patchTexCoord, UnityEngine.Bounds bounds) : base(ctx)
		{
			Bounds = bounds;
			_boundsVertices = new Bounds.BoundsVertices(bounds);
			_patchTexCoord = patchTexCoord;
			_startIndex = Ctx.Random.Next(0,
				(int) (Ctx.Settings.GetSharedBufferLength() - Ctx.Settings.GetMaxAmountBladesPerPatch()));
			_materialPropertyBlock = new MaterialPropertyBlock();
			_parameterOffsetX = (float) Ctx.Random.NextDouble();// * Ctx.Settings.InstancedGrassFactor);
			_parameterOffsetY = (float) Ctx.Random.NextDouble();// * Ctx.Settings.InstancedGrassFactor);

			_patchModelMatrix = Matrix4x4.TRS(
				new Vector3(bounds.center.x - bounds.extents.x, Ctx.Transform.position.y, bounds.center.z - bounds.extents.z),
				Quaternion.identity,
				new Vector3(Ctx.Settings.PatchSize, Ctx.DimensionsProvider.GetHeight(), Ctx.Settings.PatchSize));

			// Create the IndirectArguments Buffer
			_argsGeometryBuffer =
				new ComputeBuffer(1, _argsGeometry.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
			_argsGeometry[0] = Ctx.Settings.GetMinAmountBladesPerPatch(); //Vertex Count
			_argsGeometry[1] = Ctx.Settings.LodInstancesGeometry; //Instance Count
			_argsGeometryBuffer.SetData(_argsGeometry);

			_argsBillboardCrossedBuffer =
				new ComputeBuffer(1, _argsBillboardCrossed.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
			_argsBillboardCrossed[0] = Ctx.Settings.GetMinAmountBillboardsPerPatch(); //Vertex Count
			_argsBillboardCrossed[1] = 1; //Instance Count
			_argsBillboardCrossedBuffer.SetData(_argsBillboardCrossed);
			
			_argsBillboardScreenBuffer =
				new ComputeBuffer(1, _argsBillboardScreen.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
			_argsBillboardScreen[0] = Ctx.Settings.GetMinAmountBillboardsPerPatch(); //Vertex Count
			_argsBillboardScreen[1] = 1; //Instance Count
			_argsBillboardScreenBuffer.SetData(_argsBillboardScreen);
			CreateGrassDataTexture();
			CreateDummyMesh();
			SetupMaterialPropertyBlock();
		}

		public override bool IsLeaf { get { return true; } }

		public void Destroy()
		{
			//TODO: Clean up buffers and textures
			_argsGeometryBuffer.Release();
		}

		public void Draw()
		{
			//TODO: Add CPU LOD algorithm
			//TODO: Actually use _argsGeometryBuffer in computeShader or if CPU only, don't use Indirect Draw Methd
			//TODO: Add settings for options in computeShader
			ComputeLod();
			RunSimulationComputeShader();
			

			if (_argsGeometry[1] > 0) Graphics.DrawMeshInstancedIndirect(_dummyMesh, 0, Ctx.GrassGeometry, Bounds, _argsGeometryBuffer, 0,
				_materialPropertyBlock);
			
			if (_argsBillboardCrossed[1] > 0) Graphics.DrawMeshInstancedIndirect(_dummyMesh, 0, Ctx.GrassBillboardCrossed, Bounds, _argsBillboardCrossedBuffer, 0,
				_materialPropertyBlock);
			
			if (_argsBillboardScreen[1] > 0) Graphics.DrawMeshInstancedIndirect(_dummyMesh, 0, Ctx.GrassBillboardScreen, Bounds, _argsBillboardScreenBuffer, 0,
				_materialPropertyBlock);
		}

		private void ComputeLod()
		{
			//Distance between Camera and closest Point on BoundingBox from Camera
			var distance = Vector3.Distance(Ctx.Camera.transform.position, Bounds.ClosestPoint(Ctx.Camera.transform.position));
			
			//Calculate InstanceCounts of different LODs (Geometry, BillboardsCrossed, BillboardsScreen)
			var geometryInstanceCount = (uint) Mathf.Ceil(SingleLerp(Ctx.Settings.LodInstancesGeometry, distance,
				Ctx.Settings.LodDistanceGeometryPeak, Ctx.Settings.LodDistanceGeometryEnd));
			var billboardCrossedInstanceCount = (uint) Mathf.Ceil(DoubleLerp(Ctx.Settings.LodInstancesBillboardCrossed, distance,
				Ctx.Settings.LodDistanceBillboardCrossedStart, Ctx.Settings.LodDistanceBillboardCrossedPeak, Ctx.Settings.LodDistanceBillboardCrossedEnd));
			var billboardScreenInstanceCount = (uint) Mathf.Ceil(DoubleLerp(Ctx.Settings.LodInstancesBillboardScreen, distance,
				Ctx.Settings.LodDistanceBillboardScreenStart, Ctx.Settings.LodDistanceBillboardScreenPeak, Ctx.Settings.LodDistanceBillboardScreenEnd));
			
			_applyTransition = Ctx.Settings.EnableHeightTransition;
			
			_argsGeometry[1] = geometryInstanceCount;
			_argsBillboardCrossed[1] = billboardCrossedInstanceCount;
			_argsBillboardScreen[1] = billboardScreenInstanceCount;
			
			_argsGeometryBuffer.SetData(_argsGeometry);
			_argsBillboardCrossedBuffer.SetData(_argsBillboardCrossed);
			_argsBillboardScreenBuffer.SetData(_argsBillboardScreen);
		}

		private static float SingleLerp(uint value, float cur, float peak, float end)
		{
			var t1 = Mathf.Clamp01((cur - peak) / (end - peak));
			return value -  Mathf.LerpUnclamped(0, value, t1);
		}
		
		private static float DoubleLerp(uint value, float cur, float start, float peak, float end)
		{
			var t0 = Mathf.Clamp01((cur - start) / (peak - start));
			var t1 = Mathf.Clamp01((cur - peak) / (end - peak));
			return value - (Mathf.LerpUnclamped(value, 0, t0) + Mathf.LerpUnclamped(0, value, t1));
		}
		
		private void CreateGrassDataTexture()
		{
			
			_normalHeightTexture = new Texture2D(Ctx.Settings.GetPerPatchTextureWidthHeight(), Ctx.Settings.GetPerPatchTextureWidthHeight(),
				TextureFormat.RGBAFloat, true, true)
			{
				filterMode = Ctx.Settings.GrassDataTrilinearFiltering ? FilterMode.Trilinear : FilterMode.Bilinear,
				wrapMode = TextureWrapMode.Clamp
			};
			var textureData = new Color[Ctx.Settings.GetPerPatchTextureLength()];
			int i = 0;

			//TODO: Smooth the edges with neighbouring pixels for smooth transitions between patches.
			for (int x = 0; x < Ctx.Settings.GetPerPatchTextureWidthHeight(); x++)
			for (int y = 0; y < Ctx.Settings.GetPerPatchTextureWidthHeight(); y++)
			{
				var uvLocal = new Vector2((float)y/Ctx.Settings.GrassDataResolution, (float)x/Ctx.Settings.GrassDataResolution);
				var bladePosition = new Vector2(
					_patchTexCoord.x + _patchTexCoord.z * uvLocal.x,
					_patchTexCoord.y + _patchTexCoord.w * uvLocal.y);
				
				
				
				var posY = Ctx.HeightProvider.GetHeight(bladePosition.x, bladePosition.y) /
				           Ctx.DimensionsProvider.GetHeight();
				var up = Ctx.NormalProvider.GetNormal(bladePosition.x, bladePosition.y);
				
				textureData[i] = new Color(up.x, up.y, up.z, posY);
				i++;
			}
			
			_normalHeightTexture.SetPixels(textureData);
			_normalHeightTexture.Apply();
			
			_simulationTexture = new RenderTexture(Ctx.Settings.GetPerPatchTextureWidthHeight(), Ctx.Settings.GetPerPatchTextureWidthHeight(), 0,
				RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
			{
				filterMode = Ctx.Settings.GrassDataTrilinearFiltering ? FilterMode.Trilinear : FilterMode.Bilinear,
				autoGenerateMips = Ctx.Settings.GrassDataTrilinearFiltering,
				useMipMap = Ctx.Settings.GrassDataTrilinearFiltering,
				dimension = TextureDimension.Tex2DArray,
				volumeDepth = 2,
				enableRandomWrite = true,
				wrapMode = TextureWrapMode.Clamp
			};
			_simulationTexture.Create();

			SetupSimulation();
		}

		private void CreateDummyMesh()
		{
			//TODO: meshSize and computeshader thread count needs to be connected
			var dummyMeshSize = Ctx.Settings.GetMinAmountBladesPerPatch();
			var dummyVertices = new Vector3[dummyMeshSize];
			var indices = new int[dummyMeshSize];

			for (var i = 0; i < dummyMeshSize; i++)
			{
				dummyVertices[i] = Vector3.zero;
				indices[i] = i;
			}

			_dummyMesh = new Mesh {vertices = dummyVertices};
			_dummyMesh.SetIndices(indices, MeshTopology.Points, 0);
			_dummyMesh.RecalculateBounds();
		}

		private void SetupMaterialPropertyBlock()
		{
			//TODO: Add option to update things like matrix not only on startup but also on update
			_materialPropertyBlock.SetFloat("startIndex", _startIndex);
			_materialPropertyBlock.SetFloat("parameterOffsetX", _parameterOffsetX);
			_materialPropertyBlock.SetFloat("parameterOffsetY", _parameterOffsetY);
			_materialPropertyBlock.SetTexture("SimulationTexture", _simulationTexture);
			_materialPropertyBlock.SetTexture("NormalHeightTexture", _normalHeightTexture);
			_materialPropertyBlock.SetMatrix("patchModelMatrix", _patchModelMatrix);
		}

		private void SetupSimulation()
		{
			Ctx.GrassSimulationComputeShader.SetBool("applyTransition", _applyTransition);
			Ctx.GrassSimulationComputeShader.SetInt("startIndex", _startIndex);
			Ctx.GrassSimulationComputeShader.SetFloat("parameterOffsetX", _parameterOffsetX);
			Ctx.GrassSimulationComputeShader.SetFloat("parameterOffsetY", _parameterOffsetY);
			Ctx.GrassSimulationComputeShader.SetFloat("GrassDataResolution", Ctx.Settings.GrassDataResolution);
			Ctx.GrassSimulationComputeShader.SetMatrix("patchModelMatrix", _patchModelMatrix);
			
			//Set buffers for SimulationSetup Kernel
			Ctx.GrassSimulationComputeShader.SetTexture(Ctx.KernelSimulationSetup, "SimulationTexture", _simulationTexture);
			Ctx.GrassSimulationComputeShader.SetTexture(Ctx.KernelSimulationSetup, "NormalHeightTexture", _normalHeightTexture);
			
			uint threadGroupX, threadGroupY, threadGroupZ;
			Ctx.GrassSimulationComputeShader.GetKernelThreadGroupSizes(Ctx.KernelSimulationSetup, out threadGroupX, out threadGroupY, out threadGroupZ);
			
			//Run Physics Simulation
			Ctx.GrassSimulationComputeShader.Dispatch(Ctx.KernelSimulationSetup, (int) (Ctx.Settings.GrassDataResolution / threadGroupX), (int) (Ctx.Settings.GrassDataResolution / threadGroupY), 1);
		}
		
		private void RunSimulationComputeShader()
		{
			//Set per patch data for whole compute shader
			Ctx.GrassSimulationComputeShader.SetBool("applyTransition", _applyTransition);
			Ctx.GrassSimulationComputeShader.SetInt("startIndex", _startIndex);
			Ctx.GrassSimulationComputeShader.SetFloat("parameterOffsetX", _parameterOffsetX);
			Ctx.GrassSimulationComputeShader.SetFloat("parameterOffsetY", _parameterOffsetY);
			Ctx.GrassSimulationComputeShader.SetFloat("GrassDataResolution", Ctx.Settings.GrassDataResolution);
			Ctx.GrassSimulationComputeShader.SetMatrix("patchModelMatrix", _patchModelMatrix);

			//Set buffers for Physics Kernel
			Ctx.GrassSimulationComputeShader.SetTexture(Ctx.KernelPhysics, "SimulationTexture", _simulationTexture);
			Ctx.GrassSimulationComputeShader.SetTexture(Ctx.KernelPhysics, "NormalHeightTexture", _normalHeightTexture);

			uint threadGroupX, threadGroupY, threadGroupZ;
			Ctx.GrassSimulationComputeShader.GetKernelThreadGroupSizes(Ctx.KernelPhysics, out threadGroupX, out threadGroupY, out threadGroupZ);
			
			//Run Physics Simulation
			Ctx.GrassSimulationComputeShader.Dispatch(Ctx.KernelPhysics, (int) (Ctx.Settings.GrassDataResolution / threadGroupX), (int) (Ctx.Settings.GrassDataResolution / threadGroupY), 1);
			
			/*
			//Set buffers for Culling Kernel
			Ctx.GrassSimulationComputeShader.SetTexture(Ctx.KernelCulling, "SimulationTexture", _simulationTexture);
			Ctx.GrassSimulationComputeShader.SetTexture(Ctx.KernelCulling, "NormalHeightTexture", _normalHeightTexture);

			//Perform Culling
			//TODO: threadgroupsX correct?
			Ctx.GrassSimulationComputeShader.Dispatch(Ctx.KernelCulling, (int) (Ctx.Settings.GrassDataResolution / threadGroupX), (int) (Ctx.Settings.GrassDataResolution / threadGroupY), 1);
			*/
		}

#if UNITY_EDITOR
		public override void DrawGizmo()
		{
			if (Ctx.EditorSettings.EnablePatchGizmo)
			{
				Gizmos.color = new Color(0f, 0f, 1f, 0.5f);
				Gizmos.DrawWireSphere(Bounds.center, 0.5f);
				Gizmos.DrawWireCube(Bounds.center, Bounds.size);
			}
			if (Ctx.EditorSettings.EnableBladeUpGizmo || Ctx.EditorSettings.EnableFullBladeGizmo)
			{
				Gizmos.color = new Color(0f, 1f, 0f, 0.8f);

				for (var i = 0; i < _argsGeometry[0] * _argsGeometry[1]; i++)
				{
					var uvLocal = Ctx.SharedGrassData.UvData[_startIndex + i].Position;
					var uvGlobal = new Vector2(_parameterOffsetX, _parameterOffsetY) + uvLocal;
					var normalHeight = _normalHeightTexture.GetPixelBilinear(uvLocal.x, uvLocal.y);
					var pos = new Vector3(uvLocal.x, normalHeight.a, uvLocal.y);
					var bladeUp = new Vector3(normalHeight.r, normalHeight.g, normalHeight.b).normalized;
					pos = _patchModelMatrix.MultiplyPoint3x4(pos);
					var parameters = Ctx.SharedGrassData.ParameterTexture.GetPixelBilinear(uvGlobal.x, uvGlobal.y);

					if (Ctx.EditorSettings.EnableFullBladeGizmo)
					{
						var sd = Mathf.Sin(parameters.a);
						var cd = Mathf.Cos(parameters.a);
						var tmp = new Vector3(sd, sd + cd, cd).normalized;
						var bladeDir = Vector3.Cross(bladeUp, tmp).normalized;
						var bladeFront = Vector3.Cross(bladeUp, bladeDir).normalized;
						//var camdir = (pos - Ctx.Camera.transform.position).normalized;

						Gizmos.color = new Color(1f, 0f, 0f, 0.8f);
						Gizmos.DrawLine(pos, pos + bladeUp);
						Gizmos.color = new Color(0f, 1f, 0f, 0.8f);
						Gizmos.DrawLine(pos, pos + bladeDir);
						Gizmos.color = new Color(0f, 0f, 1f, 0.8f);
						Gizmos.DrawLine(pos, pos + bladeFront);
						//Gizmos.color = new Color(1f, 0f, 1f, 0.8f);
						//Gizmos.DrawLine(pos, pos + camdir);
					}
					if (Ctx.EditorSettings.EnableBladeUpGizmo)
					{
						Gizmos.color = new Color(1f, 0f, 0f, 0.8f);
						Gizmos.DrawLine(pos, pos + bladeUp);
					}
				}
			}
		}
#endif
	}
}