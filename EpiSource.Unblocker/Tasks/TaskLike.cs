using System;
using System.Threading.Tasks;

namespace EpiSource.Unblocker.Tasks {
    
    public interface ITaskLike<out T> {
        T Result { get; }
        
        Task WaitAsync();
    }

    public sealed class TaskLike<T> : ITaskLike<T> {
        private readonly Task<T> task;

        public TaskLike(Task<T> task) {
            this.task = task;
        }

        public T Result {
            get {
                return this.task.Result;
            }
        }
        
        public async Task WaitAsync() {
            await this.task;
        }
    }

    public static class TaskLikeExtensions {
        public static async Task<T> AsAwaitable<T>(this ITaskLike<T> taskLike) {
            await taskLike.WaitAsync();
            return taskLike.Result;
        }

        public static ITaskLike<T> AsTaskLike<T>(this Task<T> taskLike) {
            return new TaskLike<T>(taskLike);
        }

        public static ITaskLike<TOut> TransformResultAsTaskLike<TIn, TOut>(this Task<TIn> task, Func<TIn, TOut> transformation) {
            return new TaskLike<TOut>(task.TransformResult(transformation));
        }
    }
}