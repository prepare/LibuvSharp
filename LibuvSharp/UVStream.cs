using System;
using System.Text;
using System.Runtime.InteropServices;

namespace LibuvSharp
{
	unsafe public abstract class UVStream : HandleBase, IUVStream<ArraySegment<byte>>, ITryWrite<ArraySegment<byte>>
	{
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate void read_callback_unix(IntPtr stream, IntPtr size, UnixBufferStruct buf);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate void read_callback_win(IntPtr stream, IntPtr size, WindowsBufferStruct buf);

		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_read_start(IntPtr stream, alloc_callback_unix alloc_callback, read_callback_unix read_callback);

		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_read_start(IntPtr stream, alloc_callback_win alloc_callback, read_callback_win read_callback);

		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_read_watcher_start(IntPtr stream, Action<IntPtr> read_watcher_callback);

		[DllImport ("uv", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_read_stop(IntPtr stream);

		[DllImport("uv", EntryPoint = "uv_write", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_write_unix(IntPtr req, IntPtr handle, UnixBufferStruct[] bufs, int bufcnt, callback callback);

		[DllImport("uv", EntryPoint = "uv_write", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_write_win(IntPtr req, IntPtr handle, WindowsBufferStruct[] bufs, int bufcnt, callback callback);

		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_shutdown(IntPtr req, IntPtr handle, callback callback);

		uv_stream_t *stream;

		long PendingWrites { get; set; }

		public long WriteQueueSize {
			get {
				CheckDisposed();

				return stream->write_queue_size.ToInt64();
			}
		}

		ByteBufferAllocatorBase allocator;
		public ByteBufferAllocatorBase ByteBufferAllocator {
			get {
				return allocator ?? Loop.ByteBufferAllocator;
			}
			set {
				allocator = value;
			}
		}

		internal UVStream(Loop loop, IntPtr handle)
			: base(loop, handle)
		{
			stream = (uv_stream_t *)(handle.ToInt64() + Handle.Size(HandleType.UV_HANDLE));
		}

		internal UVStream(Loop loop, int size)
			: this(loop, UV.Alloc(size))
		{
		}

		internal UVStream(Loop loop, HandleType type)
			: this(loop, Handle.Size(type))
		{
		}

		internal UVStream(Loop loop, HandleType type, Func<IntPtr, IntPtr, int> constructor)
			: this(loop, type)
		{
			Construct(constructor);
		}

		internal UVStream(Loop loop, HandleType handleType, Func<IntPtr, IntPtr, int, int> constructor, int arg1)
			: this(loop, handleType)
		{
			Construct(constructor, arg1);
		}

		internal UVStream(Loop loop, HandleType handleType, Func<IntPtr, IntPtr, int, int, int> constructor, int arg1, int arg2)
			: this(loop, handleType)
		{
			Construct(constructor, arg1, arg2);
		}

		public void Resume()
		{
			CheckDisposed();

			int r;
			if (UV.isUnix) {
				r = uv_read_start(NativeHandle, ByteBufferAllocator.AllocCallbackUnix, read_cb_unix);
			} else {
				r = uv_read_start(NativeHandle, ByteBufferAllocator.AllocCallbackWin, read_cb_win);
			}
			Ensure.Success(r);
		}

		public void Pause()
		{
			Invoke(uv_read_stop);
		}

		static read_callback_unix read_cb_unix = read_callback_u;
		static void read_callback_u(IntPtr streamPointer, IntPtr size, UnixBufferStruct buf)
		{
			var stream = FromIntPtr<UVStream>(streamPointer);
			stream.read_callback(streamPointer, size);
		}

		static read_callback_win read_cb_win = read_callback_w;
		static void read_callback_w(IntPtr streamPointer, IntPtr size, WindowsBufferStruct buf)
		{
			var stream = FromIntPtr<UVStream>(streamPointer);
			stream.read_callback(streamPointer, size);
		}

		void read_callback(IntPtr streamPointer, IntPtr size)
		{
			long nread = size.ToInt64();
			if (nread == 0) {
				return;
			} else if (nread < 0) {
				if (UVException.Map((int)nread) == UVErrorCode.EOF) {
					Close(Complete);
				} else {
					OnError(Ensure.Map((int)nread));
					Close();
				}
			} else {
				OnData(ByteBufferAllocator.Retrieve(size.ToInt32()));
			}
		}

		protected virtual void OnComplete()
		{
			if (Complete != null) {
				Complete();
			}
		}

		public event Action Complete;

		protected virtual void OnError(Exception exception)
		{
			if (Error != null) {
				Error(exception);
			}
		}

		public event Action<Exception> Error;

		protected virtual void OnData(ArraySegment<byte> data)
		{
			if (Data != null) {
				Data(data);
			}
		}

		public event Action<ArraySegment<byte>> Data;

		void OnDrain()
		{
			if (Drain != null) {
				Drain();
			}
		}

		public event Action Drain;

		public void Write(ArraySegment<byte> data, Action<Exception> callback)
		{
			CheckDisposed();

			int index = data.Offset;
			int count = data.Count;

			PendingWrites++;

			GCHandle datagchandle = GCHandle.Alloc(data.Array, GCHandleType.Pinned);
			CallbackPermaRequest cpr = new CallbackPermaRequest(RequestType.UV_WRITE);
			cpr.Callback = (status, cpr2) => {
				datagchandle.Free();
				PendingWrites--;

				Ensure.Success(status, callback);

				if (PendingWrites == 0) {
					OnDrain();
				}
			};

			var ptr = (IntPtr)(datagchandle.AddrOfPinnedObject().ToInt64() + index);

			int r;
			if (UV.isUnix) {
				UnixBufferStruct[] buf = new UnixBufferStruct[1];
				buf[0] = new UnixBufferStruct(ptr, count);
				r = uv_write_unix(cpr.Handle, NativeHandle, buf, 1, CallbackPermaRequest.CallbackDelegate);
			} else {
				WindowsBufferStruct[] buf = new WindowsBufferStruct[1];
				buf[0] = new WindowsBufferStruct(ptr, count);
				r = uv_write_win(cpr.Handle, NativeHandle, buf, 1, CallbackPermaRequest.CallbackDelegate);
			}

			Ensure.Success(r);
		}

		public void Shutdown(Action<Exception> callback)
		{
			CheckDisposed();

			var cbr = new CallbackPermaRequest(RequestType.UV_SHUTDOWN);
			cbr.Callback = (status, _) => {
				Ensure.Success(status, (ex) => Close(() => {
					if (callback != null) {
						callback(ex);
					}
				}));
			};
			uv_shutdown(cbr.Handle, NativeHandle, CallbackPermaRequest.CallbackDelegate);
		}

		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_is_readable(IntPtr handle);

		internal bool readable;
		public bool Readable {
			get {
				CheckDisposed();

				return uv_is_readable(NativeHandle) != 0;
			}
			set {
				readable = value;
			}
		}

		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int uv_is_writable(IntPtr handle);

		internal bool writeable;
		public bool Writeable {
			get {
				CheckDisposed();

				return uv_is_writable(NativeHandle) != 0;
			}
			set {
				writeable = value;
			}
		}


		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal extern static int uv_try_write(IntPtr handle, WindowsBufferStruct[] bufs, int nbufs);
		[DllImport("uv", CallingConvention = CallingConvention.Cdecl)]
		internal extern static int uv_try_write(IntPtr handle, UnixBufferStruct[] bufs, int nbufs);

		unsafe public int TryWrite(ArraySegment<byte> data)
		{
			CheckDisposed();

			Ensure.ArgumentNotNull(data.Array, "data");

			fixed (byte* bytePtr = data.Array) {
				IntPtr ptr = (IntPtr)bytePtr + data.Offset;
				int r;
				if (UV.isUnix) {
					UnixBufferStruct[] buf = new UnixBufferStruct[1];
					buf[0] = new UnixBufferStruct(ptr, data.Count);
					r = uv_try_write(NativeHandle, buf, 1);
				} else {
					WindowsBufferStruct[] buf = new WindowsBufferStruct[1];
					buf[0] = new WindowsBufferStruct(ptr, data.Count);
					r = uv_try_write(NativeHandle, buf, 1);
				}
				Ensure.Success(r);
				return r;
			}
		}
	}
}

