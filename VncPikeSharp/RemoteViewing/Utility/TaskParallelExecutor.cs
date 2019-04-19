using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteViewing.Utility
{
    public class TaskParallelExecutor : List<Action>
    {
        public event EventHandler ProcessEvent;
        public List<Task> Tasks = new List<Task>();

        public void Start(int max, int timeOut)
        {
            if (Count == 0)
                return;

            LimitedConcurrencyLevelTaskScheduler lcts = new LimitedConcurrencyLevelTaskScheduler(max);
            TaskFactory factory = new TaskFactory(lcts);
            Exception exception = null;

            foreach (Action action in this)
            {
                Task task = factory.StartNew(action)
                    .ContinueWith(
                    c =>
                    {
                        AggregateException ae = c.Exception;
                        if (ae.InnerException != null)
                            exception = ae.InnerException;
                    },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously
                );
                Tasks.Add(task);
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();

            while (sw.ElapsedMilliseconds < timeOut || timeOut == 0)
            {
                if (Tasks.All(o => o.IsCompleted))
                    break;

                Thread.Sleep(200);

                if (ProcessEvent != null)
                    ProcessEvent(this, EventArgs.Empty);
            }

            if (exception != null)
                throw exception;
        }
    }
}
