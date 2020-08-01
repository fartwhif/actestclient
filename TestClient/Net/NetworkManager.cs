using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

using TestClient.Crypto;
using TestClient.Net.Messages;
using TestClient.Net.Packets;
using TestClient.Util;

namespace TestClient.Net
{
	public class NetworkManager : IDisposable
	{
		public static NetworkManager Instance { get; private set; }

		public const string ProtocolVersion = "2011.10.001";
		private const long TicksPerMillisecond = 10000;

		private static long GetTicks() => DateTimeOffset.UtcNow.Ticks / TicksPerMillisecond;

		private IPEndPoint localAddr;
		private Socket connection;

		private KeyExchange keyExchange;

		private Thread networkThread;
		private bool shouldBeRunning;

		private uint eventCount;
		private uint fragmentCount;
		private long startTicks;

		private ServerInfo loginServer;
		private ServerInfo currentWorldServer;
		private List<ServerInfo> worldServers;

		public ushort RunningTime => (ushort)((GetTicks() - startTicks) / 500);
		public string World { get; set; }

		public bool Configured { get; private set; }

		private ConcurrentDictionary<uint, FragmentedPacket> pendingFragments;

		#region Events

		//public event EventHandler<FragmentedPacketEventArgs> FragmentCompleted;
		public event EventHandler<MessageEventArgs> MessageReceived;

		#endregion

		#region Packet Queue

		private struct PacketQueueEntry
		{
			public Packet Packet;
			public ServerInfo Server;
			public bool IncludeSequence;
			public bool IncrementSequence;
			public PacketFlags Flags;

			public PacketQueueEntry(ServerInfo si, Packet p, bool include, bool increment, PacketFlags flags)
			{
				Server = si;
				Packet = p;
				IncludeSequence = include;
				IncrementSequence = increment;
				Flags = flags;
			}
		}

		private ConcurrentQueue<PacketQueueEntry> packetQueue;
		private AutoResetEvent packetReady;

		#endregion

		#region Ctor/Dispose

		public NetworkManager()
		{
			this.keyExchange = new KeyExchange();
			this.keyExchange.InitClient();

			this.worldServers = new List<ServerInfo>();
			this.pendingFragments = new ConcurrentDictionary<uint, FragmentedPacket>();
			this.packetQueue = new ConcurrentQueue<PacketQueueEntry>();
			this.packetReady = new AutoResetEvent(false);



			this.World = "Disconnected";

			Instance = this;
		}

		// public NetworkManager(int port)
		// 	: this()
		// {
		// 	localAddr = new IPEndPoint(IPAddress.Any, port);
		// }

		// public NetworkManager(IPEndPoint localAddress)
		// 	: this()
		// {
		// 	localAddr = localAddress;
		// }

		~NetworkManager()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected void Dispose(bool disposing)
		{
			shouldBeRunning = false;
			if (disposing)
			{
				this.connection?.Dispose();
			}
		}

		#endregion

		#region Connect

		public void Configure()
		{
			Configure(new IPEndPoint(IPAddress.Any, 0));
		}

		public void Configure(int port)
		{
			Configure(new IPEndPoint(IPAddress.Any, port));
		}

		public void Configure(IPEndPoint endPoint)
		{
			this.localAddr = endPoint;
			Configured = true;
		}

		public void Connect(string server, int port, string account, string token)
		{
			Reset();

			this.loginServer = new ServerInfo(server, port);
			this.currentWorldServer = this.loginServer;

			// save connection info

			if (this.networkThread != null)
			{
				this.shouldBeRunning = false;
				this.NetworkThreadEnding.WaitOne();
			}
			this.networkThread = new Thread(NetworkThreadStart);
			this.networkThread.Start();

			LoginRequestPacket cp = new LoginRequestPacket(account, token);
			SendMessage(cp, false, true);

			this.World = "Connecting...";
		}

		private void Reset()
		{
			this.worldServers.Clear();
			this.packetQueue.Clear();

			this.eventCount = 1;
			this.fragmentCount = 1;
			this.startTicks = GetTicks();
		}

		#endregion

		#region Send

		public void SendEventMessage<T>(T message) where T : EventFragment
		{
			message.Sequence = eventCount++;
			SendMessage(currentWorldServer, message);
		}

		public void SendMessage<T>(T fragment) where T : Fragment
		{
			SendMessage(currentWorldServer, fragment);
		}

		public void SendMessage<T>(ServerInfo si, T fragment) where T : Fragment
		{
			fragment.Header.Sequence = fragmentCount++;
			fragment.Header.Count = 1;
			Packet p = new Packet();
			p.AddFragment(fragment);
			SendMessage(si, p, true, true, PacketFlags.Fragmented);
		}

		public void SendMessage<T>(T packet, bool includeSequence, bool incrementSequence) where T : Packet
		{
			SendMessage(this.loginServer, packet, includeSequence, incrementSequence, PacketFlags.Default);
		}

		public void SendMessage<T>(ServerInfo si, T packet, bool includeSequence, bool incrementSequence) where T : Packet
		{
			SendMessage(si, packet, includeSequence, incrementSequence, PacketFlags.Fragmented);
		}

		public void SendMessage<T>(ServerInfo si, T packet, bool includeSequence, bool incrementSequence, PacketFlags flags) where T : Packet
		{
			packetQueue.Enqueue(new PacketQueueEntry(si, packet, includeSequence, incrementSequence, flags));
			packetReady.Set();
		}

		private void Send(ServerInfo si, byte[] data, int length, bool useRead)
		{
			Debug.WriteLine($"Sending Packet with ServerFlags = {si.Flags:G}");
			this.connection.SendTo(data, length, SocketFlags.None,
				useRead ? si.ReadAddress : si.WriteAddress);
		}

		// private void Send<T>(ServerInfo si, T packet) where T : Packet
		// {
		// 	Send(si, packet, 1);
		// }

		// private void Send<T>(ServerInfo si, T packet, int count) where T : Packet
		// {
		// 	byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
		// 	int sendLength = 0;
		// 	try
		// 	{
		// 		using (MemoryStream ms = new MemoryStream(buffer, true))
		// 		{
		// 			sendLength = packet.Serialize(ms);
		// 		}
		// 		for (int i = 0; i < count; i++)
		// 		{
		// 			Send(si, buffer, sendLength, false);
		// 		}
		// 	}
		// 	finally
		// 	{
		// 		ArrayPool<byte>.Shared.Return(buffer, true);
		// 	}
		// }

		private void Send<T>(ServerInfo si, T packet, bool includeSequence, bool incrementSequence, PacketFlags flags) where T : Packet
		{
			if (incrementSequence)
				si.SendCount++;

			byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
			int sendLength = 0;
			try
			{
				if (si.RecvCount > si.LastAckSent)
				{
					packet.SetAck(si.RecvCount);
					si.LastAckSent = si.RecvCount;
				}

				using (MemoryStream ms = new MemoryStream(buffer, true))
				{
					sendLength = packet.Serialize(ms);
				}

				Span<byte> pBuffer = buffer;
				Span<TransitHeader> pHeader = MemoryMarshal.Cast<byte, TransitHeader>(pBuffer);
				ref TransitHeader header = ref MemoryMarshal.GetReference(pHeader);

				if (includeSequence)
				{
					header.Sequence = si.SendCount;
					header.Time = RunningTime;
				}
				else
				{
					header.Sequence = 0;
					header.Time = 0;
				}

				if (si.ClientId > 0)
				//if (si.Flags.HasFlag(ServerFlags.Connected))
				{
					header.Id = si.ClientId;
					header.Table = si.Table;
				}


				if ((header.Flags & flags) != 0)
				{
					if (si.Flags.HasFlag(ServerFlags.ChecksumSeeds))
					{
						header.Checksum = packet.Hash(si.SendGenerator.GetSendKey(), buffer);
						// uint crc = 0;
						// uint xor = 0;

						// crc = Checksum.Calculate200(buffer);
						// xor = si.SendGenerator.GetSendKey();

						// crc ^= xor;
						// crc += Checksum.CalculateTransport(buffer);

						// header.Checksum = xor;
					}
					else
					{
						Debug.WriteLine("Fragment without CRC seed");
						// broken
						if (incrementSequence)
							si.SendCount--;
					}
				}
				else
				{
					//uint old_csum = Checksum.Calculate(buffer);
					//uint new_csum = packet.Hash(0, buffer);
					//Debug.WriteLine($"CRC {old_csum:X8} => {new_csum:X8}");
					header.Checksum = packet.Hash(0, buffer);
				}

				bool useRead = packet.Header.Flags.HasFlag(PacketFlags.ConnectResponse);
				Send(si, buffer, sendLength, useRead);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer, true);
			}
		}

		#endregion

		#region Receive

		private void DoReceive()
		{
			IPEndPoint ip = new IPEndPoint(IPAddress.Any, 0);
			EndPoint ep = ip;

			byte[] buffer = ArrayPool<byte>.Shared.Rent(2048);

			currentRead = this.connection.BeginReceiveFrom(
				buffer, 0, buffer.Length,
				SocketFlags.None,
				ref ep,
				OnReceive,
				new object[] { this.connection, buffer });
		}

		IAsyncResult currentRead = null;

		private void OnReceive(IAsyncResult result)
		{
			//Debug.WriteLine("OnReceive");

			if (!result.IsCompleted)
				if (!result.AsyncWaitHandle.WaitOne(10000))
					throw new ApplicationException("Network connection failure");

			IPEndPoint ip = new IPEndPoint(IPAddress.Any, 0);
			EndPoint ep = ip;

			//is this true? https://stackoverflow.com/a/4662631/6620171
			//some way to test for disposed socket?
			int length;
			byte[] buffer = null;
			try
			{
				var myState = (object[])result.AsyncState;
				Socket p = (Socket)myState[0];
				length = p.EndReceiveFrom(result, ref ep);
				buffer = (byte[])myState[1];
			}
			catch { }
			finally
			{
				if (shouldBeRunning)
					DoReceive();
			}


			ip = (IPEndPoint)ep;
			//Debug.WriteLine($"Data from {ip.Address.ToString()}:{ip.Port}");

			try
			{
				//Debug.WriteLine($"Data from {ip.Address.ToString()}");
				if (this.loginServer.IsFrom(ip))
				{
					ProcessPacket(buffer, this.loginServer);
				}
				else
				{
					foreach (ServerInfo si in this.worldServers)
					{
						if (si.IsFrom(ip))
						{
							ProcessPacket(buffer, si);
							break;
						}
					}
				}
			}
			finally
			{
				if (buffer != null)
				{
					ArrayPool<byte>.Shared.Return(buffer, true);
				}
			}
		}

		private void ProcessPacket(byte[] buffer, ServerInfo si)
		{
			//Debug.WriteLine("ProcessPacket");

			int position = 0;
			Span<byte> pBuffer = buffer;

			TransitHeader header = pBuffer.Read<TransitHeader>(ref position);
			int length = position + header.Size;

			if (header.Sequence > si.RecvCount)
			{
				// request resends if necessary

				// just cheat for now
				si.RecvCount = header.Sequence;
			}

			if (!si.Flags.HasFlag(ServerFlags.Connected) || header.Table != si.Table)
			{
				// error?
			}

			// process and clear some informational flags
			PacketFlags type = header.Flags;
			//Debug.WriteLine(type.ToString("G"));

			if (type.HasFlag(PacketFlags.Resent))
				type &= ~PacketFlags.Resent;

			// this needs to be handled
			if (type.HasFlag(PacketFlags.Disconnect))
				type &= ~PacketFlags.Disconnect;

			if (type.HasFlag(PacketFlags.Checksum))
			{
				// validate checksum

				type &= ~PacketFlags.Checksum;
			}

			// these are probably standalone
			// switch (4,4)
			// referral (8,2,2,4,8,2,2,4)

			// per ACE the c2s order is:
			// server switch (8)
			// retransmit (4 + 4*)
			// reject (4 + 4*)
			// ack (4)
			// login ??
			// worldlogin (8)
			// connect response (8)
			// command (8)
			// timesync (8)
			// echo (4)
			// flow (6)

			// s2c order:
			// ack (4)
			// time (8)
			// echo (4)

			if (type.HasFlag(PacketFlags.AckSequence))
			{
				uint acked = pBuffer.Read<uint>(ref position);
				si.LastAckRecv = acked;

				type &= ~PacketFlags.AckSequence;
			}

			if (type.HasFlag(PacketFlags.TimeSync))
			{
				double serverTime = pBuffer.Read<double>(ref position);

				si.ServerTime = serverTime;
				si.LastSyncRecv = DateTimeOffset.UtcNow.Ticks;
				si.Flags |= ServerFlags.Sync;

				type &= ~PacketFlags.TimeSync;
			}

			if (type.HasFlag(PacketFlags.EchoResponse))
			{
				float delta = pBuffer.Read<float>(ref position);

				type &= ~PacketFlags.EchoResponse;
			}

			if (type.HasFlag(PacketFlags.FlowUpdate))
			{
				//Debug.WriteLine("Flow");

				uint unk1 = pBuffer.Read<uint>(ref position);
				ushort unk2 = pBuffer.Read<ushort>(ref position);

				type &= ~PacketFlags.FlowUpdate;
			}

			// if (type.HasFlag(PacketFlags.NetError))
			// {
			// 	Debug.WriteLine("Error");

			// 	uint errorCode = pBuffer.Read<uint>(ref position);

			// 	type &= ~PacketFlags.NetError;
			// }

			// if (si.Flags.HasFlag(ServerFlags.Connecting))
			// {
			// 	si.Flags &= ~ServerFlags.Connecting;
			// 	si.Flags |= ServerFlags.Connected;
			// }

			//Debug.WriteLine(Enum.Format(typeof(PacketFlags), type, "G"));
			switch (type)
			{
				case PacketFlags.ConnectRequest:
					//Debug.WriteLine("Connection Request");
					{
						long serverTime = pBuffer.Read<long>(ref position);
						long cookie = pBuffer.Read<long>(ref position);
						uint clientId = pBuffer.Read<uint>(ref position);
						uint seed_s2c = pBuffer.Read<uint>(ref position);
						uint seed_c2s = pBuffer.Read<uint>(ref position);
						uint unk = pBuffer.Read<uint>(ref position);

						si.SetSeeds(seed_s2c, seed_c2s);
						si.ServerId = header.Id;
						si.ClientId = (ushort)clientId;
						si.Table = header.Table;
						si.Flags |= ServerFlags.Connecting;

						//Debug.WriteLine($"{serverTime:X16} {cookie:X16} {clientId:X4} {seed_s2c:X8} {seed_c2s:X8} {unk:X4}");

						SendMessage(new ConnectResponsePacket(cookie), false, false);
					}
					break;

				case PacketFlags.LoginWorld:
					break;

				case PacketFlags.Fragmented:
					if (si.Flags.HasFlag(ServerFlags.Connecting))
					{
						si.Flags &= ~ServerFlags.Connecting;
						si.Flags |= ServerFlags.Connected;
					}
					// send to fragment handler
					// we can have multiple fragments in one packet
					while (position < length)
					{
						FragmentHeader fragHeader = pBuffer.Read<FragmentHeader>(ref position);
						FragmentedPacket frag = null;
						if (!pendingFragments.TryGetValue(fragHeader.Sequence, out frag))
						{
							frag = new FragmentedPacket(fragHeader.Sequence, fragHeader.Count);
							pendingFragments.TryAdd(fragHeader.Sequence, frag);
						}
						int fragLength = fragHeader.Size - FragmentHeader.SizeOf;
						frag.AddChunk(pBuffer.Slice(position, fragLength), fragHeader.Index, fragLength);
						position += fragLength;
						//Debug.WriteLine($"Adding Fragment {fragHeader.Sequence} {fragHeader.Index+1}/{fragHeader.Count} {fragLength}");

						if (frag.Count == frag.Received)
						{
							// parse it!
							Message msg = MessageParser.Parse(frag, Messages.MessageDirection.Inbound);
							pendingFragments.TryRemove(fragHeader.Sequence, out frag);

							// this is not a great way to handle this, since message needs to be disposed,
							// but i don't want to couple network manager to the message handler at this time.
							// review in the future to implement a better interface for transfering this
							// information to another subsystem.
							if (MessageReceived != null)
								MessageReceived(this, new MessageEventArgs(msg, MessageDirection.Inbound));
						}
					}
					break;

				case PacketFlags.Default:
					break;

				default:
					Debug.WriteLine($"Invalid Packet State {type:G}");
					break;
			}
		}

		#endregion

		ManualResetEvent NetworkThreadEnding = new ManualResetEvent(false);

		#region Network Thread

		private void NetworkThreadStart()
		{
			Debug.WriteLine("Network Thread Start");

			this.connection = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			this.connection.Bind(this.localAddr);
			this.connection.EnableConnectionResetWrapper(false);
			this.shouldBeRunning = true;

			DoReceive();

			try
			{
				Debug.WriteLine("Network Loop Start");
				while (shouldBeRunning)
				{
					packetReady.WaitOne(TimeSpan.FromMilliseconds(250));
					// anything in the queue
					if (this.packetQueue.TryDequeue(out PacketQueueEntry entry))
					{
						Debug.WriteLine("Sending packet from queue");
						// send it
						Send(entry.Server, entry.Packet, entry.IncludeSequence, entry.IncrementSequence, entry.Flags);
					}

					// ping things that need pinging
					long currentTicks = GetTicks();
					long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

					// TODO: flag them first so a regular send can include the time
					// fallback here if nothing is pending
					SendSync(loginServer, currentTicks, currentTicks);
					foreach (ServerInfo si in worldServers)
					{
						SendSync(si, currentTicks, currentTicks);
					}

					// let other things happen
					Thread.Yield();
				}
			}
			catch (ThreadAbortException)
			{
			}
			finally
			{
				if (this.connection != null)
				{
					//if (currentRead != null)
					//{
					//	if (!currentRead.IsCompleted)
					//	{
					//		this.currentRead.AsyncWaitHandle.WaitOne();
					//	}
					//}
					try
					{
						this.connection.Close();
					}
					catch { }
					finally
					{
						this.connection = null;
					}
				}
				this.connection = null;
				this.NetworkThreadEnding.Set();
				this.shouldBeRunning = false;
			}
		}

		private void SendSync(ServerInfo si, long currentTicks, long currentTime)
		{
			if (si.Flags.HasFlag(ServerFlags.Connected))
			{
				long delta = currentTicks - si.LastSyncSent;
				if (delta >= 30000)
				{
					//Debug.WriteLine($"Server tick delta {delta} current {currentTicks}");
					Send(si, new TimeSyncPacket(currentTime), false, false, PacketFlags.Default);
					si.LastSyncSent = currentTicks;
				}

				delta = currentTicks - si.LastPing;
				if (delta >= 10000)
				{
					SendMessage(si, new PingEventFragment());
					si.LastPing = currentTicks;
				}
			}
		}

		#endregion
	}
}
