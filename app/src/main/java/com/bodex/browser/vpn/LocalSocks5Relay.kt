package com.bodex.browser.vpn

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Loopback SOCKS5 bridge for Chromium.
 *
 * Chromium's SOCKS5 client does not implement RFC 1929 username/password
 * authentication. This listener accepts unauthenticated local SOCKS5 clients
 * and performs the upstream username/password negotiation itself.
 *
 * Bind only to 127.0.0.1. The app's VPN configuration excludes this app so
 * the bridge's upstream socket cannot loop back into the TUN.
 */
object LocalSocks5Relay {
    const val PORT = 10808
    private const val CONNECT_TIMEOUT_MS = 12_000
    private const val HANDSHAKE_TIMEOUT_MS = 15_000

    private val running = AtomicBoolean(false)
    private val workers: ExecutorService = Executors.newCachedThreadPool()
    @Volatile private var listener: ServerSocket? = null

    @Synchronized
    fun start(host: String, port: Int, username: String, password: String): Boolean {
        stop()
        if (host.isBlank() || port !in 1..65535) return false

        return try {
            val server = ServerSocket()
            server.reuseAddress = true
            server.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), PORT), 32)
            listener = server
            running.set(true)

            workers.execute {
                while (running.get()) {
                    try {
                        val client = server.accept()
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
            false
        }
    }

    @Synchronized
    fun stop() {
        running.set(false)
        try { listener?.close() } catch (_: Exception) {}
        listener = null
    }

    fun isRunning(): Boolean = running.get() && listener?.isBound == true && listener?.isClosed == false

    private fun handleClient(
        client: Socket,
        upstreamHost: String,
        upstreamPort: Int,
        username: String,
        password: String
    ) {
        var upstream: Socket? = null
        try {
            client.soTimeout = HANDSHAKE_TIMEOUT_MS
            val clientIn = DataInputStream(BufferedInputStream(client.getInputStream()))
            val clientOut = BufferedOutputStream(client.getOutputStream())

            // Negotiate a local no-auth SOCKS5 session with Chromium.
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

            // Read and preserve Chromium's CONNECT request.
            val header = ByteArray(4)
            clientIn.readFully(header)
            if (header[0].toInt() != 5) throw EOFException("Invalid SOCKS request")
            if (header[1].toInt() != 1) {
                clientOut.write(byteArrayOf(5, 7, 0, 1, 0, 0, 0, 0, 0, 0))
                clientOut.flush()
                return
            }

            val addressTail = when (header[3].toInt() and 0xFF) {
                1 -> ByteArray(6).also { clientIn.readFully(it) } // IPv4 + port
                3 -> {
                    val length = clientIn.readUnsignedByte()
                    byteArrayOf(length.toByte()) + ByteArray(length + 2).also { clientIn.readFully(it) }
                }
                4 -> ByteArray(18).also { clientIn.readFully(it) } // IPv6 + port
                else -> throw EOFException("Unsupported SOCKS address type")
            }
            val request = header + addressTail

            upstream = Socket()
            upstream.connect(InetSocketAddress(upstreamHost, upstreamPort), CONNECT_TIMEOUT_MS)
            upstream.soTimeout = HANDSHAKE_TIMEOUT_MS
            val upstreamIn = DataInputStream(BufferedInputStream(upstream.getInputStream()))
            val upstreamOut = BufferedOutputStream(upstream.getOutputStream())

            val hasCredentials = username.isNotEmpty() && password.isNotEmpty()
            val userBytes = username.toByteArray(Charsets.UTF_8)
            val passBytes = password.toByteArray(Charsets.UTF_8)
            if (hasCredentials && (userBytes.size > 255 || passBytes.size > 255)) {
                throw IllegalArgumentException("SOCKS5 credentials exceed 255 bytes")
            }

            // Negotiate the upstream proxy's supported authentication method.
            upstreamOut.write(if (hasCredentials) byteArrayOf(5, 2, 0, 2) else byteArrayOf(5, 1, 0))
            upstreamOut.flush()
            val methodReply = ByteArray(2)
            upstreamIn.readFully(methodReply)
            if (methodReply[0].toInt() != 5) throw EOFException("Invalid upstream SOCKS response")

            when (methodReply[1].toInt() and 0xFF) {
                0 -> if (hasCredentials) {
                    // A credentialed proxy may allow no-auth; continue if it explicitly does.
                }
                2 -> {
                    if (!hasCredentials) throw EOFException("Upstream proxy requires credentials")
                    val auth = byteArrayOf(1, userBytes.size.toByte()) + userBytes +
                        byteArrayOf(passBytes.size.toByte()) + passBytes
                    upstreamOut.write(auth)
                    upstreamOut.flush()
                    val authReply = ByteArray(2)
                    upstreamIn.readFully(authReply)
                    if (authReply[0].toInt() != 1 || authReply[1].toInt() != 0) {
                        throw EOFException("Upstream SOCKS5 authentication failed")
                    }
                }
                else -> throw EOFException("Upstream SOCKS5 rejected authentication methods")
            }

            upstreamOut.write(request)
            upstreamOut.flush()

            // Relay the complete variable-length SOCKS5 CONNECT reply.
            val replyHeader = ByteArray(4)
            upstreamIn.readFully(replyHeader)
            val replyTail = when (replyHeader[3].toInt() and 0xFF) {
                1 -> ByteArray(6).also { upstreamIn.readFully(it) }
                3 -> {
                    val length = upstreamIn.readUnsignedByte()
                    byteArrayOf(length.toByte()) + ByteArray(length + 2).also { upstreamIn.readFully(it) }
                }
                4 -> ByteArray(18).also { upstreamIn.readFully(it) }
                else -> throw EOFException("Invalid upstream SOCKS address type")
            }
            clientOut.write(replyHeader)
            clientOut.write(replyTail)
            clientOut.flush()
            if (replyHeader[1].toInt() != 0) return

            client.soTimeout = 0
            upstream.soTimeout = 0
            val upstreamSocket = upstream
            val clientSocket = client

            // Two independent pumps keep the full-duplex TCP stream flowing.
            workers.execute { pump(clientIn, upstreamOut, upstreamSocket) }
            pump(upstreamIn, clientOut, clientSocket)
        } catch (_: Exception) {
            // Fail closed: never retry the destination directly.
        } finally {
            try { client.close() } catch (_: Exception) {}
            try { upstream?.close() } catch (_: Exception) {}
        }
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
