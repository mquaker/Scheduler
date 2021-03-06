﻿using Ashkatchap.Scheduler.Collections;
using Ashkatchap.Scheduler.Logging;
using System;
using System.Threading;
using UnityEngine.Profiling;

namespace Ashkatchap.Scheduler {
	internal partial class FrameUpdater {
		public class WorkerManager {
			internal readonly JobArray[] jobsToDo; // Only "thread" writes, others only read
			internal readonly Worker[] workers;
			internal readonly WorkerBase workerMainThread;

			private UnorderedList<QueuedJob> pool = new UnorderedList<QueuedJob>();
			
			internal volatile byte lastActionStamp = 0;

			
			public WorkerManager(Updater updater) {
				jobsToDo = new JobArray[256];
				for (int i = 0; i < jobsToDo.Length; i++) jobsToDo[i] = new JobArray();

				Thread.MemoryBarrier();

				int numWorkers = Math.Max(1, Scheduler.AVAILABLE_CORES - 1);
				workers = new Worker[numWorkers];
				for (int i = 0; i < workers.Length; i++) workers[i] = new Worker(this, i + 1);
				workerMainThread = new WorkerBase(0);

				updater.AddUpdateCallback(Cleaner, 255);
			}

			internal void OnDestroy() {
				for (int i = 0; i < workers.Length; i++) workers[i].OnDestroy();
			}

			
			private void SignalWorkers() {
				int l = Scheduler.CORES_IN_USE - 1;
				for (int i = 0; i < l; i++) workers[workers.Length - 1 - i].waiter.Set();
			}

			public int GetNumberWorkers() {
				return workers.Length;
			}

			
			
			public JobReference QueueMultithreadJobInstance(Job job, ushort numberOfIterations, byte priority) {
				Logger.WarnAssert(!Scheduler.InMainThread(), "QueueMultithreadJobInstance can only be called from the main thread");
				if (!Scheduler.InMainThread()) return default(JobReference);

				if (Scheduler.FORCE_SINGLE_THREAD) {
					for (int i = 0; i < numberOfIterations; i++) {
						job(i);
					}
					return new JobReference(null);
				} else {
					var queuedJob = pool.Size > 0 ? pool.ExtractLast() : new QueuedJob();
					queuedJob.Init(job, numberOfIterations, priority);

					jobsToDo[queuedJob.priority].AddAuto(queuedJob.priority, queuedJob);
					lastActionStamp++;
					
					SignalWorkers();
					return new JobReference(queuedJob);
				}
			}
			
			internal void SetJobToAllThreads(QueuedJob queuedJob) {
				JobPriorityChange(queuedJob, 0);
			}

			internal void JobPriorityChange(QueuedJob queuedJob, byte newPriority) {
				Logger.WarnAssert(!Scheduler.InMainThread(), "JobPriorityChange can only be called from the main thread");
				if (!Scheduler.InMainThread()) return;
				
				Thread.MemoryBarrier();
				jobsToDo[queuedJob.priority].RemoveAuto(queuedJob.priority, queuedJob);
				queuedJob.priority = newPriority;
				jobsToDo[newPriority].AddAuto(newPriority, queuedJob);
				lastActionStamp++;

				SignalWorkers();
			}



			private void Cleaner() {
				Profiler.BeginSample("Cleaner");
				// Search and Pool finished jobs
				for (int p = 0; p < jobsToDo.Length; p++) {
					for (int i = jobsToDo[p].count - 1; i >= 0; i--) {
						var job = jobsToDo[p].array[i];
						if (job.IsFinished()) {
							jobsToDo[p].RemoveAtAuto(p, i);
							pool.Add(job);
						}
					}
				}
				Profiler.EndSample();
			}
		}

		/// <summary>
		/// Not thread safe
		/// </summary>
		internal class JobArray {
			const int INITIAL_SIZE = 64;
			const int INCREMENT = 64;

			public QueuedJob[] array;
			public int count;

			public JobArray() {
				array = new QueuedJob[INITIAL_SIZE];
				count = 0;
			}

			private void IncreaseArray() {
				QueuedJob[] newArray = new QueuedJob[array.Length + INCREMENT];
				Logger.Trace("Array of multithreaded jobs increased from " + array.Length + " to " + newArray.Length);
				array.CopyTo(newArray, 0);
				array = newArray;
			}

			public void AddAuto(int queue, QueuedJob queuedJob) {
				if (queue == 0) {
					InsertDesplacingOlder(0, queuedJob);
				} else {
					Add(queuedJob);
				}
			}
			private void InsertDesplacingOlder(int index, QueuedJob queuedJob) {
				if (count == array.Length) IncreaseArray();

				for (int i = count - 1; i >= index; i--) array[i + 1] = array[i];
				array[index] = queuedJob;
				count++;
			}
			private void Add(QueuedJob queuedJob) {
				if (count == array.Length) IncreaseArray();
				array[count] = queuedJob;
				count++;
			}

			public void RemoveAuto(int queue, QueuedJob queuedJob) {
				if (queue == 0) {
					RemoveDisplacingOlder(queuedJob);
				} else {
					Remove(queuedJob);
				}
			}
			private void RemoveDisplacingOlder(QueuedJob queuedJob) {
				for (int i = 0; i < count; i++) {
					if (array[i] == queuedJob) {
						RemoveAtDisplacingOlder(i);
						return;
					}
				}
				Logger.Error("Trying to remove but it does not exist!");
			}
			private void Remove(QueuedJob queuedJob) {
				for (int i = 0; i < count; i++) {
					if (array[i] == queuedJob) {
						RemoveAt(i);
						return;
					}
				}
				Logger.Error("Trying to remove but it does not exist!");
			}

			public void RemoveAtAuto(int queue, int index) {
				if (queue == 0) {
					RemoveAtDisplacingOlder(index);
				} else {
					RemoveAt(index);
				}
			}
			private void RemoveAtDisplacingOlder(int index) {
				for (int i = index; i < count - 1; i++) array[i] = array[i + 1];
				array[count - 1] = null;
				count--;
			}
			private void RemoveAt(int index) {
				array[index] = array[count - 1];
				array[count - 1] = null;
				count--;
			}
		}
	}
}
