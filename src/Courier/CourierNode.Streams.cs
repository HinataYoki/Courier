using System;
using System.Collections.Generic;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的流数据面生命周期。
    /// </summary>
    public sealed partial class CourierNode
    {
        private readonly Dictionary<string, CourierStreamWriter> mStreams =
            new Dictionary<string, CourierStreamWriter>(StringComparer.Ordinal);
        private readonly object mStreamGate = new object();

        /// <summary>
        /// 为当前端点打开一个流。流描述写在实现私有目录，不改变 endpoint.json。
        /// </summary>
        /// <param name="streamName">流名称，使用小写字母、数字和连字符。</param>
        /// <param name="options">容量配置；为 null 时使用默认值。</param>
        /// <returns>可写入的流。释放返回值只会关闭该流，不关闭端点。</returns>
        public CourierStreamWriter OpenStream(string streamName, CourierStreamOptions? options = null)
        {
            ThrowIfDisposed();
            if (!CourierStreamDescriptor.IsValidStreamName(streamName))
            {
                throw new ArgumentException("invalid stream name", nameof(streamName));
            }

            lock (mStreamGate)
            {
                ThrowIfDisposed();
                if (mStreams.TryGetValue(streamName, out CourierStreamWriter? existing))
                {
                    throw new InvalidOperationException("courier stream is already open: " + streamName);
                }

                var writer = new CourierStreamWriter(mEndpointDirectory, streamName, mSessionId, options);
                mStreams.Add(streamName, writer);
                return writer;
            }
        }

        /// <summary>关闭本端点打开的全部流；幂等。</summary>
        internal void DisposeStreams()
        {
            CourierStreamWriter[] writers;
            lock (mStreamGate)
            {
                if (mStreams.Count == 0) return;
                writers = new CourierStreamWriter[mStreams.Count];
                mStreams.Values.CopyTo(writers, 0);
                mStreams.Clear();
            }

            for (int index = 0; index < writers.Length; index++)
            {
                writers[index].Dispose();
            }
        }
    }
}
