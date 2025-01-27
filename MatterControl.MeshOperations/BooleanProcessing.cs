﻿/*
Copyright (c) 2019, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DualContouring;
using g3;
using gs;
using MatterHackers.Agg;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.PolygonMesh;
using MatterHackers.VectorMath;

namespace MatterHackers.PolygonMesh
{
	public static class BooleanProcessing
	{
		public enum CsgModes
		{
			Union,
			Subtract,
			Intersect
		}

		public enum IplicitSurfaceMethod
		{
			[Description("Faster but less accurate")]
			Grid,
			[Description("Slower but more accurate")]
			Exact
		};

		public enum ProcessingModes
		{
			Polygons,
			Marching_Cubes,
			Dual_Contouring,
		}

		public enum ProcessingResolution
		{
			_64 = 6,
			_128 = 7,
			_256 = 8,
			_512 = 9,
		}

		private const string BooleanAssembly = "609_Boolean_bin.dll";

		[DllImport(BooleanAssembly, CallingConvention = CallingConvention.Cdecl)]
		public static extern int DeleteDouble(ref IntPtr handle);

		[DllImport(BooleanAssembly, CallingConvention = CallingConvention.Cdecl)]
		public static extern int DeleteInt(ref IntPtr handle);

		[DllImport(BooleanAssembly, CallingConvention = CallingConvention.Cdecl)]
		public static extern void DoBooleanOperation(double[] va, int vaCount, int[] fa, int faCount, double[] vb, int vbCount, int[] fb, int fbCount, int operation, out IntPtr pVc, out int vcCount, out IntPtr pVf, out int vfCount);

		public static Mesh DoArray(IEnumerable<(Mesh mesh, Matrix4X4 matrix)> items,
			CsgModes operation,
			ProcessingModes processingMode,
			ProcessingResolution inputResolution,
			ProcessingResolution outputResolution,
			IProgress<ProgressStatus> reporter,
			CancellationToken cancellationToken)
		{
			if (processingMode == ProcessingModes.Polygons)
			{
				var progressStatus = new ProgressStatus();
				var totalOperations = items.Count() - 1;
				double amountPerOperation = 1.0 / totalOperations;
				double percentCompleted = 0;

				var first = true;
				var resultsMesh = items.First().mesh;
				var firstWorldMatrix = items.First().matrix;

				foreach (var item in items)
				{
					if (first)
					{
						first = false;
						continue;
					}

					var itemWorldMatrix = item.matrix;
					resultsMesh = Do(item.mesh,
						itemWorldMatrix,
						// other mesh
						resultsMesh,
						firstWorldMatrix,
						// operation
						operation,
						processingMode,
						inputResolution,
						outputResolution,
						// reporting
						reporter,
						amountPerOperation,
						percentCompleted,
						progressStatus,
						cancellationToken);

					// after the first union we are working with the transformed mesh and don't need the first transform
					firstWorldMatrix = Matrix4X4.Identity;

					percentCompleted += amountPerOperation;
					progressStatus.Progress0To1 = percentCompleted;
					reporter?.Report(progressStatus);
				}

				return resultsMesh;
			}
			else
			{
				var implicitMeshs = new List<BoundedImplicitFunction3d>();
				foreach (var item in items)
				{
					var meshCopy = item.mesh.Copy(CancellationToken.None);
					meshCopy.Transform(item.matrix);

					implicitMeshs.Add(GetImplicitFunction(meshCopy, processingMode == ProcessingModes.Polygons, 1 << (int)inputResolution));
				}

				DMesh3 GenerateMeshF(BoundedImplicitFunction3d root, int numCells)
				{
					var bounds = root.Bounds();

					var c = new MarchingCubesPro()
					{
						Implicit = root,
						RootMode = MarchingCubesPro.RootfindingModes.LerpSteps,      // cube-edge convergence method
						RootModeSteps = 5,                                        // number of iterations
						Bounds = bounds,
						CubeSize = bounds.MaxDim / numCells,
					};

					c.Bounds.Expand(3 * c.CubeSize);                            // leave a buffer of cells
					c.Generate();

					MeshNormals.QuickCompute(c.Mesh);                           // generate normals
					return c.Mesh;
				}

				switch (operation)
				{
					case CsgModes.Union:
						if (processingMode == ProcessingModes.Dual_Contouring)
						{
							var union = new ImplicitNaryUnion3d()
							{
								Children = implicitMeshs
							};
							var bounds = union.Bounds();
							var size = bounds.Max - bounds.Min;
							var root = Octree.BuildOctree((pos) =>
							{
								var pos2 = new Vector3d(pos.X, pos.Y, pos.Z);
								return union.Value(ref pos2);
							}, new Vector3(bounds.Min.x, bounds.Min.y, bounds.Min.z),
							new Vector3(size.x, size.y, size.z),
							(int)outputResolution,
							.001);
							return Octree.GenerateMeshFromOctree(root);
						}
						else
						{
							return GenerateMeshF(new ImplicitNaryUnion3d()
							{
								Children = implicitMeshs
							}, 1 << (int)outputResolution).ToMesh();
						}

					case CsgModes.Subtract:
						{
							if (processingMode == ProcessingModes.Dual_Contouring)
							{
								var subtract = new ImplicitNaryIntersection3d()
								{
									Children = implicitMeshs
								};
								var bounds = subtract.Bounds();
								var root = Octree.BuildOctree((pos) =>
								{
									var pos2 = new Vector3d(pos.X, pos.Y, pos.Z);
									return subtract.Value(ref pos2);
								}, new Vector3(bounds.Min.x, bounds.Min.y, bounds.Min.z),
								new Vector3(bounds.Width, bounds.Depth, bounds.Height),
								(int)outputResolution,
								.001);
								return Octree.GenerateMeshFromOctree(root);
							}
							else
							{
								return GenerateMeshF(new ImplicitNaryDifference3d()
								{
									A = implicitMeshs.First(),
									BSet = implicitMeshs.GetRange(0, implicitMeshs.Count - 1)
								}, 1 << (int)outputResolution).ToMesh();
							}
						}

					case CsgModes.Intersect:
						if (processingMode == ProcessingModes.Dual_Contouring)
						{
							var intersect = new ImplicitNaryIntersection3d()
							{
								Children = implicitMeshs
							};
							var bounds = intersect.Bounds();
							var root = Octree.BuildOctree((pos) =>
							{
								var pos2 = new Vector3d(pos.X, pos.Y, pos.Z);
								return intersect.Value(ref pos2);
							}, new Vector3(bounds.Min.x, bounds.Min.y, bounds.Min.z),
							new Vector3(bounds.Width, bounds.Depth, bounds.Height),
							(int)outputResolution,
							.001);
							return Octree.GenerateMeshFromOctree(root);
						}
						else
						{
							return GenerateMeshF(new ImplicitNaryIntersection3d()
							{
								Children = implicitMeshs
							}, 1 << (int)outputResolution).ToMesh();
						}
				}
			}

			return null;
		}

		public static Mesh Do(Mesh inMeshA,
			Matrix4X4 matrixA,
			// mesh B
			Mesh inMeshB,
			Matrix4X4 matrixB,
			// operation
			CsgModes operation,
			ProcessingModes processingMode,
			ProcessingResolution inputResolution,
			ProcessingResolution outputResolution,
			// reporting
			IProgress<ProgressStatus> reporter,
			double amountPerOperation,
			double percentCompleted,
			ProgressStatus progressStatus,
			CancellationToken cancellationToken)
		{
			bool externalAssemblyExists = File.Exists(BooleanAssembly);
			if (processingMode == ProcessingModes.Polygons)
			{
				// only try to run the improved booleans if we are 64 bit and it is there
				if (externalAssemblyExists && IntPtr.Size == 8)
				{
					IntPtr pVc = IntPtr.Zero;
					IntPtr pFc = IntPtr.Zero;
					try
					{
						double[] va;
						int[] fa;
						va = inMeshA.Vertices.ToDoubleArray(matrixA);
						fa = inMeshA.Faces.ToIntArray();
						double[] vb;
						int[] fb;
						vb = inMeshB.Vertices.ToDoubleArray(matrixB);
						fb = inMeshB.Faces.ToIntArray();

						DoBooleanOperation(va,
							va.Length,
							fa,
							fa.Length,
							// object B
							vb,
							vb.Length,
							fb,
							fb.Length,
							// operation
							(int)operation,
							// results
							out pVc,
							out int vcCount,
							out pFc,
							out int fcCount);

						var vcArray = new double[vcCount];
						Marshal.Copy(pVc, vcArray, 0, vcCount);

						var fcArray = new int[fcCount];
						Marshal.Copy(pFc, fcArray, 0, fcCount);

						return new Mesh(vcArray, fcArray);
					}
					catch (Exception ex)
					{
						//ApplicationController.Instance.LogInfo("Error performing boolean operation: ");
						//ApplicationController.Instance.LogInfo(ex.Message);
					}
					finally
					{
						if (pVc != IntPtr.Zero)
						{
							DeleteDouble(ref pVc);
						}

						if (pFc != IntPtr.Zero)
						{
							DeleteInt(ref pFc);
						}

						if (progressStatus != null)
						{
							progressStatus.Progress0To1 = percentCompleted + amountPerOperation;
							reporter.Report(progressStatus);
						}
					}
				}
				else
				{
					Console.WriteLine($"libigl skipped - AssemblyExists: {externalAssemblyExists}; Is64Bit: {IntPtr.Size == 8};");

					var meshA = inMeshA.Copy(CancellationToken.None);
					meshA.Transform(matrixA);

					var meshB = inMeshB.Copy(CancellationToken.None);
					meshB.Transform(matrixB);

					switch (operation)
					{
						case CsgModes.Union:
							return Csg.CsgOperations.Union(meshA,
								meshB,
								(status, progress0To1) =>
								{
									// Abort if flagged
									cancellationToken.ThrowIfCancellationRequested();
									progressStatus.Status = status;
									progressStatus.Progress0To1 = percentCompleted + (amountPerOperation * progress0To1);
									reporter?.Report(progressStatus);
								},
								cancellationToken);
						
						case CsgModes.Subtract:
							return Csg.CsgOperations.Subtract(meshA,
								meshB,
								(status, progress0To1) =>
								{
									progressStatus.Status = status;
									progressStatus.Progress0To1 = percentCompleted + (amountPerOperation * progress0To1);
									reporter?.Report(progressStatus);
								},
								cancellationToken);
						
						case CsgModes.Intersect:
							return Csg.CsgOperations.Intersect(meshA,
								meshB,
								(status, progress0To1) =>
								{
									// Abort if flagged
									cancellationToken.ThrowIfCancellationRequested(); progressStatus.Status = status;
									progressStatus.Progress0To1 = percentCompleted + (amountPerOperation * progress0To1);
									reporter.Report(progressStatus);
								},
								cancellationToken);
					}
				}
			}
			else
			{
				var meshA = inMeshA.Copy(CancellationToken.None);
				meshA.Transform(matrixA);
				
				var meshB = inMeshB.Copy(CancellationToken.None);
				meshB.Transform(matrixB);

				if (meshA.Faces.Count < 4)
				{
					return meshB;
				}
				else if (meshB.Faces.Count < 4)
				{
					return meshA;
				}
				
				var implicitA = GetImplicitFunction(inMeshA, processingMode == ProcessingModes.Polygons, (int)inputResolution);
				var implicitB = GetImplicitFunction(inMeshB, processingMode == ProcessingModes.Polygons, (int)inputResolution);

				DMesh3 GenerateMeshF(BoundedImplicitFunction3d root, int numCells)
				{
					var bounds = root.Bounds();

					var c = new MarchingCubes()
					{
						Implicit = root,
						RootMode = MarchingCubes.RootfindingModes.LerpSteps,      // cube-edge convergence method
						RootModeSteps = 5,                                        // number of iterations
						Bounds = bounds,
						CubeSize = bounds.MaxDim / numCells,
					};

					c.Bounds.Expand(3 * c.CubeSize);                            // leave a buffer of cells
					c.Generate();

					MeshNormals.QuickCompute(c.Mesh);                           // generate normals
					return c.Mesh;
				}

				var marchingCells = 1 << (int)outputResolution;
				switch (operation)
				{
					case CsgModes.Union:
						return GenerateMeshF(new ImplicitUnion3d()
						{
							A = implicitA,
							B = implicitB
						}, marchingCells).ToMesh();

					case CsgModes.Subtract:
						return GenerateMeshF(new ImplicitDifference3d()
						{
							A = implicitA,
							B = implicitB
						}, marchingCells).ToMesh();

					case CsgModes.Intersect:
						return GenerateMeshF(new ImplicitIntersection3d()
						{
							A = implicitA,
							B = implicitB
						}, marchingCells).ToMesh();
				}
			}

			return null;
		}

		class MWNImplicit : BoundedImplicitFunction3d
		{
			public DMeshAABBTree3 MeshAABBTree3;
			public AxisAlignedBox3d Bounds() { return MeshAABBTree3.Bounds; }
			public double Value(ref Vector3d pt)
			{
				return -(MeshAABBTree3.FastWindingNumber(pt) - 0.5);
			}
		}


		public static BoundedImplicitFunction3d GetImplicitFunction(Mesh mesh, bool exact, int numCells)
		{
			var meshA3 = mesh.ToDMesh3();

			// Interesting experiment, this produces an extremely accurate surface representation but is quite slow (even though fast) compared to voxel lookups.
			if (exact)
			{
				DMeshAABBTree3 meshAABBTree3 = new DMeshAABBTree3(meshA3, true);
				meshAABBTree3.FastWindingNumber(Vector3d.Zero);   // build approximation
				return new MWNImplicit()
				{
					MeshAABBTree3 = meshAABBTree3
				};
			}
			else
			{
				double meshCellsize = meshA3.CachedBounds.MaxDim / numCells;
				var signedDistance = new MeshSignedDistanceGrid(meshA3, meshCellsize);
				signedDistance.Compute();
				return new DenseGridTrilinearImplicit(signedDistance.Grid, signedDistance.GridOrigin, signedDistance.CellSize);
			}
		}
	}
}