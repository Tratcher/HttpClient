using System;
using System.IO;

namespace Microsoft.Net.Http.Client
{
    /// <summary>
    /// Summary description for ApmStream
    /// </summary>
    public abstract class ApmStream : Stream
    {
#if DNXCORE50
        public abstract IAsyncResult BeginRead(byte[] buffer, int offset, int size, AsyncCallback callback, Object state);

        public abstract int EndRead(IAsyncResult asyncResult);

        public abstract IAsyncResult BeginWrite(byte[] buffer, int offset, int size, AsyncCallback callback, Object state);

        public abstract void EndWrite(IAsyncResult asyncResult);
#endif
    }
}