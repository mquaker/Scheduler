﻿#pragma warning disable 420

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ashkatchap.Updater {
	internal class QueuedJob {
		private static int lastId = 0;


		#region EXECUTOR_RW WORKER_RW
		private Job job;
		private volatile int doneIndices;
		private ushort length;
		#endregion

		private readonly Range[] indices;

		#region EXECUTOR_RW WORKER_R
		private int temporalId;
		#endregion

		#region EXECUTOR_RW
		internal byte priority;
		#endregion


		public QueuedJob() {
			indices = new Range[Scheduler.ProcessorCount];
			for (int i = 0; i < indices.Length; i++) indices[i] = new Range();
		}


		internal void Init(Job job, ushort length, byte priority) {
			Logger.WarnAssert(!Scheduler.InMainThread(), "Init can only be called from the main thread");
			if (!Scheduler.InMainThread()) return;
				
			Thread.MemoryBarrier();
			this.job = job;
			temporalId = lastId++;
			this.priority = priority;
			Thread.MemoryBarrier();
			for (int i = 0; i < indices.Length - 1; i++) indices[i].Set(0, 1, 0);
			indices[indices.Length - 1].Set(0, 0, length - 1);
			doneIndices = 0;
			Thread.MemoryBarrier();
			this.length = length;
			Thread.MemoryBarrier();

			Logger.TraceVerbose("[" + temporalId + "] job created");
		}

		internal bool TryExecute(int workerIndex, ref KeyValuePair<int, int>[] tmp) {
			int startIndex = -1, index = -1, lastIndex = -1;
			var ind = indices[workerIndex];

			// Get next index if available
			lock (ind) {
				lastIndex = ind.lastIndex;
				if (ind.index <= lastIndex) {
					startIndex = ind.startIndex;
					index = ind.index;
					++ind.index;
				}
			}

			if (index == -1) {
				// our range does not have more indices left. Get more indices from another range
				// First, without locking we sort all the threads by the range available
				if (tmp == null || tmp.Length != indices.Length - 1) {
					tmp = new KeyValuePair<int, int>[indices.Length - 1];
				}

				bool somethingFound = false;
				for (int i = 0, j = 0; i < indices.Length; i++) {
					if (i == workerIndex) continue;
					var otherInd = indices[i];
					int range = otherInd.lastIndex + 1 - otherInd.index;
					tmp[j++] = new KeyValuePair<int, int>(i, range);
					if (range > 0) somethingFound = true;
				}

				if (somethingFound) {
					// sort by range
					Array.Sort(tmp, sortByRange);
					
					for (int i = 0; i < tmp.Length; i++) {
						var otherInd = indices[tmp[i].Key];
						if (otherInd.index < otherInd.lastIndex) {
							int stolenIndex = -1;
							int stolenLastIndex = -1;
							lock (otherInd) {
								if (otherInd.index < otherInd.lastIndex) {
									// This thread has enough indices, we can steal some of them
									int range = otherInd.lastIndex + 1 - otherInd.index;

									stolenLastIndex = otherInd.lastIndex;
									otherInd.lastIndex = otherInd.index + range / 2 - 1;
									stolenIndex = otherInd.index + range / 2;
								}
							}
							if (stolenIndex != -1) {
								lock (ind) {
									startIndex = ind.startIndex = stolenIndex;
									index = stolenIndex;
									ind.index = stolenIndex + 1;
									lastIndex = ind.lastIndex = stolenLastIndex;
								}
								break;
							}
						}
					}
				}
			}

			if (index == -1) {
				return false;
			}

			
			try {
				job(index);
			} catch (Exception e) {
				Logger.Error(e.ToString());
			} finally {
				if (index == lastIndex) {
					// if we end a range update doneIndices with the size of the range
					int rangeDone = lastIndex - startIndex + 1;
					int f = Interlocked.Add(ref doneIndices, rangeDone); // increment when the current batch is done, not every step
					if (f == length) {
						Logger.TraceVerbose("[" + temporalId + "] job finished");
					}
				}
			}
						
			return true;
		}

		// DESC order
		private static Comparison<KeyValuePair<int, int>> sortByRange = (a, b) => {
			return b.Value - a.Value;
		};

		private KeyValuePair<int, int>[] tmpForMainThread;
		public void WaitForFinish() {
			Logger.ErrorAssert(!Scheduler.InMainThread(), "WaitForFinish can only be called from the main thread");
			if (!Scheduler.InMainThread()) return;

			if (Scheduler.executor == null) {
				Logger.WarnAssert(!Scheduler.InMainThread(), "Multithreading is not enabled right now");
				return;
			}
			Scheduler.executor.SetJobToAllThreads(this);
			while (true) if (!TryExecute(indices.Length - 1, ref tmpForMainThread)) break;
			while (!IsFinished()) {
				Thread.SpinWait(20);
			}
		}

		public void Destroy() {
			Logger.ErrorAssert(!Scheduler.InMainThread(), "Init can only be called from the main thread");
			if (!Scheduler.InMainThread()) return;

			doneIndices = length;

			for (int i = 0; i < indices.Length; i++) {
				lock (indices[i]) {
					indices[i].Set(0, 1, 0);
				}
			}
			Thread.MemoryBarrier();
		}

		public bool IsFinished() {
			int f = doneIndices;
			if (f == length) {
				return true;
			} else if (f > length) {
				Logger.Error("doneIndices is greater than length");
				return true;
			}
			return false;
		}

		public void ChangePriority(byte newPriority) {
			Logger.ErrorAssert(!Scheduler.InMainThread(), "ChangePriority can only be called from the main thread");
			if (!Scheduler.InMainThread()) return;

			if (Scheduler.executor == null) {
				Logger.WarnAssert(!Scheduler.InMainThread(), "Multithreading is not enabled right now");
				return;
			}

			Scheduler.executor.JobPriorityChange(this, newPriority);
		}

		public bool CheckId(int id) {
			return temporalId == id;
		}
		public int GetId() {
			return temporalId;
		}






		public class Range {
			public volatile int startIndex;
			public volatile int index;
			public volatile int lastIndex;
			
			public void Set(int startIndex, int index, int lastIndex) {
				this.startIndex = startIndex;
				this.index = index;
				this.lastIndex = lastIndex;
			}
		}
	}
}