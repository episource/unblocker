using System;
using System.Threading.Tasks;

namespace EpiSource.Unblocker.Tasks {
    public static class TaskExtensions {

        public static async Task<TOut> TransformResult<TIn, TOut>(this Task<TIn> task, Func<TIn, TOut> transformation) {
            var input = await task;
            return transformation.Invoke(input);
        }
    }
}