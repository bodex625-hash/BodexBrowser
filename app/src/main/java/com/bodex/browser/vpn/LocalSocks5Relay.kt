package com.bodex.browser.vpn

import android.net.VpnService
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/**
 * Local SOCKS5 bridge used by HEV, not by Chromium directly.
 *
 * HEV sends TCP CONNECT and UDP ASSOCIATE to this loopback server. The bridge
 * authenticates to the real upstream SOCKS5 server and forwards both TCP and
 * UDP. Every upstream socket is protected from the Android VPN before connect,
 * while browser traffic itself remains inside the TUN.
 */
object LocalSocks5Relay {
    const val PORT = 10808
    private const val CONNECT_TIMEOUT_MS = 12_000
    private const val HANDSHAKE_TIMEOUT_MS = 15_000

    private val running = AtomicBoolean(false)
    private val workers: ExecutorService = Executors.newCachedThreadPool()
    private val activeSockets = ConcurrentHashMap.newKeySet<Socket>()
    private val activeDatagrams = ConcurrentHashMap.newKeySet<DatagramSocket>()

    @Volatile private var listener: ServerSocket? = null
    @Volatile private var vpnService: VpnService? = null

    @Synchronized
    fun start(
        host: String,
        port: Int,
        username: String,
        password: String,
        service: VpnService
    ): Boolean {
        stop()
        if (host.isBlank() || port !in 1..65535) return false

        return try {
            vpnService = service
            val server = ServerSocket()
            server.reuseAddress = true
            server.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), PORT), 32)
            listener = server
            running.set(true)

            workers.execute {
                while (running.get()) {
                    try {
                        val client = server.accept()
                        activeSockets.add(client)
                        workers.execute { handleClient(client, host, port, username, password) }
                    } catch (_: Exception) {
                        if (running.get()) stop()
                    }
                }
            }
            true
        } catch (_: Exception) {
            running.set(false)
            try { listener?.close() } catch (_: Exception) {}
            listener = null
            vpnService = null
            false
        }
    }

    @Synchronized
    fun stop() {
        running.set(false)
        try { listener?.close() } catch (_: Exception) {}
        listener = null
        activeSockets.toList().forEach { try { it.close() } catch (_: Exception) {} }
        activeSockets.clear()
        activeDatagrams.toList().forEach { try { it.close() } catch (_: Exception) {} }
        activeDatagrams.clear()
        vpnService = null
    }

    fun isRunning(): Boolean =
        running.get() && listener?.isBound == true && listener?.isClosed == false

    private fun handleClient(
        client: Socket,
        upstreamHost: String,
        upstreamPort: Int,
        username: String,
        password: String
    ) {
        var upstream: Socket? = null
        var localUdp: DatagramSocket? = null
        var upstreamUdp: DatagramSocket? = null
        try {
            client.soTimeout = HANDSHAKE_TIMEOUT_MS
            val clientIn = DataInputStream(BufferedInputStream(client.getInputStream()))
            val clientOut = BufferedOutputStream(client.getOutputStream())

            // HEV connects to this loopback endpoint without authentication.
            if (clientIn.readUnsignedByte() != 5) throw EOFException("Unsupported SOCKS version")
            val methodCount = clientIn.readUnsignedByte()
            var noAuthOffered = false
            repeat(methodCount) {
                if (clientIn.readUnsignedByte() == 0) noAuthOffered = true
            }
            if (!noAuthOffered) {
                clientOut.write(byteArrayOf(5, 0xFF.toByte()))
                clientOut.flush()
                return
            }
            clientOut.write(byteArrayOf(5, 0))
            clientOut.flush()

            val requestHeader = ByteArray(4)
            clientIn.readFully(requestHeader)
            if (requestHeader[0].toInt() != 5) throw EOFException("Invalid SOCKS request")
            val command = requestHeader[1].toInt() and 0xFF
            val addressTail = readAddressTail(clientIn, requestHeader[3].toInt() and 0xFF)
            val request = requestHeader + addressTail

            if (command != 1 && command != 3) {
                writeFailure(clientOut, 7)
                return
            }

            val upstreamSocket = Socket()
            upstream = upstreamSocket
            val service = vpnService ?: throw EOFException("VPN service is unavailable")
            if (!service.protect(upstreamSocket)) {
                throw EOFException("Could not protect upstream TCP socket from VPN routing")
            }
            upstreamSocket.connect(InetSocketAddress(upstreamHost, upstreamPort), CONNECT_TIMEOUT_MS)
            upstreamSocket.soTimeout = HANDSHAKE_TIMEOUT_MS
            val upstreamIn = DataInputStream(BufferedInputStream(upstreamSocket.getInputStream()))
            val upstreamOut = BufferedOutputStream(upstreamSocket.getOutputStream())
            negotiateUpstream(upstreamIn, upstreamOut, username, password)

            if (command == 1) {
                upstreamOut.write(request)
                upstreamOut.flush()
                val reply = readSocksReply(upstreamIn)
                clientOut.write(reply)
                clientOut.flush()
                if ((reply[1].toInt() and 0xFF) != 0) return

                client.soTimeout = 0
                upstream.soTimeout = 0
                workers.execute { pump(clientIn, upstreamOut, upstreamSocket) }
                pump(upstreamIn, clientOut, client)
            } else {
                // Create a fresh upstream UDP association; do not reuse the
                // client's local BND address because it is only loopback.
                upstreamOut.write(byteArrayOf(5, 3, 0, 1, 0, 0, 0, 0, 0, 0))
                upstreamOut.flush()
                val reply = readSocksReply(upstreamIn)
                if ((reply[1].toInt() and 0xFF) != 0) {
                    clientOut.write(reply)
                    clientOut.flush()
                    return
                }

                val relay = parseRelayAddress(reply, upstreamSocket.inetAddress)
                val local = DatagramSocket(null)
                local.reuseAddress = true
                local.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0))
                localUdp = local
                activeDatagrams.add(local)

                val remote = DatagramSocket(null)
                if (!service.protect(remote)) {
                    throw EOFException("Could not protect upstream UDP socket from VPN routing")
                }
                remote.bind(InetSocketAddress(0))
                remote.connect(relay.first, relay.second)
                upstreamUdp = remote
                activeDatagrams.add(remote)

                val localPort = local.localPort
                clientOut.write(
                    byteArrayOf(
                        5, 0, 0, 1, 127, 0, 0, 1,
                        ((localPort ushr 8) and 0xFF).toByte(),
                        (localPort and 0xFF).toByte()
                    )
                )
                clientOut.flush()
                client.soTimeout = 0

                val clientEndpoint = AtomicReference<SocketAddress?>(null)
                val localSocket = local
                val remoteSocket = remote
                val allowedClientAddress = client.inetAddress

                workers.execute {
                    val buffer = ByteArray(65_535)
                    while (running.get() && !localSocket.isClosed && !remoteSocket.isClosed) {
                        try {
                            val packet = DatagramPacket(buffer, buffer.size)
                            localSocket.receive(packet)
                            if (packet.address != allowedClientAddress || packet.length < 4) continue
                            if (packet.data[0].toInt() != 0 || packet.data[1].toInt() != 0 ||
                                packet.data[2].toInt() != 0
                            ) continue
                            clientEndpoint.set(packet.socketAddress)
                            remoteSocket.send(DatagramPacket(packet.data, packet.offset, packet.length))
                        } catch (_: Exception) {
                            if (localSocket.isClosed || remoteSocket.isClosed) break
                        }
                    }
                }

                workers.execute {
                    val buffer = ByteArray(65_535)
                    while (running.get() && !localSocket.isClosed && !remoteSocket.isClosed) {
                        try {
                            val packet = DatagramPacket(buffer, buffer.size)
                            remoteSocket.receive(packet)
                            val destination = clientEndpoint.get() ?: continue
                            localSocket.send(
                                DatagramPacket(packet.data, packet.offset, packet.length, destination)
                            )
                        } catch (_: Exception) {
                            if (localSocket.isClosed || remoteSocket.isClosed) break
                        }
                    }
                }

                // RFC 1928 keeps the TCP control connection alive for the
                // lifetime of the UDP association.
                while (clientIn.read() != -1) { /* wait for client close */ }
            }
        } catch (_: Exception) {
            try { writeFailure(BufferedOutputStream(client.getOutputStream()), 1) } catch (_: Exception) {}
        } finally {
            try { client.close() } catch (_: Exception) {}
            try { upstream?.close() } catch (_: Exception) {}
            try { localUdp?.close() } catch (_: Exception) {}
            try { upstreamUdp?.close() } catch (_: Exception) {}
            activeSockets.remove(client)
            localUdp?.let { activeDatagrams.remove(it) }
            upstreamUdp?.let { activeDatagrams.remove(it) }
        }
    }

    private fun negotiateUpstream(
        input: DataInputStream,
        output: OutputStream,
        username: String,
        password: String
    ) {
        val userBytes = username.toByteArray(Charsets.UTF_8)
        val passBytes = password.toByteArray(Charsets.UTF_8)
        val hasCredentials = username.isNotEmpty() && password.isNotEmpty()
        if (hasCredentials && (userBytes.size > 255 || passBytes.size > 255)) {
            throw IllegalArgumentException("SOCKS5 credentials exceed 255 bytes")
        }

        output.write(if (hasCredentials) byteArrayOf(5, 2, 0, 2) else byteArrayOf(5, 1, 0))
        output.flush()
        val methodReply = ByteArray(2)
        input.readFully(methodReply)
        if (methodReply[0].toInt() != 5) throw EOFException("Invalid upstream SOCKS response")

        when (methodReply[1].toInt() and 0xFF) {
            0 -> Unit
            2 -> {
                if (!hasCredentials) throw EOFException("Upstream proxy requires credentials")
                val auth = byteArrayOf(1, userBytes.size.toByte()) + userBytes +
                    byteArrayOf(passBytes.size.toByte()) + passBytes
                output.write(auth)
                output.flush()
                val authReply = ByteArray(2)
                input.readFully(authReply)
                if (authReply[0].toInt() != 1 || authReply[1].toInt() != 0) {
                    throw EOFException("Upstream SOCKS5 authentication failed")
                }
            }
            else -> throw EOFException("Upstream SOCKS5 rejected authentication methods")
        }
    }

    private fun readSocksReply(input: DataInputStream): ByteArray {
        val header = ByteArray(4)
        input.readFully(header)
        if (header[0].toInt() != 5) throw EOFException("Invalid upstream SOCKS reply")
        return header + readAddressTail(input, header[3].toInt() and 0xFF)
    }

    /** Returns address bytes followed by the two-byte port, excluding ATYP. */
    private fun readAddressTail(input: DataInputStream, atyp: Int): ByteArray =
        when (atyp) {
            1 -> ByteArray(6).also { input.readFully(it) }
            3 -> {
                val length = input.readUnsignedByte()
                byteArrayOf(length.toByte()) + ByteArray(length + 2).also { input.readFully(it) }
            }
            4 -> ByteArray(18).also { input.readFully(it) }
            else -> throw EOFException("Unsupported SOCKS address type")
        }

    private fun parseRelayAddress(reply: ByteArray, fallback: InetAddress): Pair<InetAddress, Int> {
        val atyp = reply[3].toInt() and 0xFF
        val tail = reply.copyOfRange(4, reply.size)
        val address: InetAddress
        val portOffset: Int
        when (atyp) {
            1 -> {
                address = InetAddress.getByAddress(tail.copyOfRange(0, 4))
                portOffset = 4
            }
            4 -> {
                address = InetAddress.getByAddress(tail.copyOfRange(0, 16))
                portOffset = 16
            }
            3 -> {
                val length = tail[0].toInt() and 0xFF
                address = InetAddress.getByName(String(tail, 1, length, Charsets.US_ASCII))
                portOffset = 1 + length
            }
            else -> throw EOFException("Invalid upstream UDP relay address")
        }
        val port = ((tail[portOffset].toInt() and 0xFF) shl 8) or
            (tail[portOffset + 1].toInt() and 0xFF)
        val usableAddress = if (address.isAnyLocalAddress) fallback else address
        if (port == 0) throw EOFException("Upstream SOCKS5 returned an empty UDP relay port")
        return usableAddress to port
    }

    private fun writeFailure(output: OutputStream, code: Int) {
        output.write(byteArrayOf(5, code.toByte(), 0, 1, 0, 0, 0, 0, 0, 0))
        output.flush()
    }

    private fun pump(input: InputStream, output: OutputStream, destination: Socket) {
        try {
            val buffer = ByteArray(16 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                output.write(buffer, 0, count)
                output.flush()
            }
        } catch (_: Exception) {
        } finally {
            try { destination.shutdownOutput() } catch (_: Exception) {}
        }
    }
}
