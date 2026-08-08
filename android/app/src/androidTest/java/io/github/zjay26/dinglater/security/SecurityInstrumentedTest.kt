package io.github.zjay26.dinglater.security

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import io.github.zjay26.dinglater.data.DingLaterDatabase
import io.github.zjay26.dinglater.data.MessageRepository
import io.github.zjay26.dinglater.model.CapturedMessage
import io.github.zjay26.dinglater.model.PayloadQuality
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class SecurityInstrumentedTest {
    @Test
    fun aesGcmRoundTripUsesRandomNonceAndDoesNotEmbedPlaintext() {
        val secretBox = SecretBox()
        val plaintext = "测试明文-Emoji🙂-换行\n第二行"

        val first = secretBox.encrypt(plaintext)
        val second = secretBox.encrypt(plaintext)

        assertEquals(plaintext, secretBox.decrypt(first))
        assertEquals(plaintext, secretBox.decrypt(second))
        assertFalse(first.contentEquals(second))
        assertFalse(String(first, StandardCharsets.ISO_8859_1).contains(plaintext))
    }

    @Test
    fun hmacIsStableWithoutExposingInput() {
        val fingerprints = FingerprintService()
        val first = fingerprints.fingerprint("会话", "发送者", "测试明文")
        val second = fingerprints.fingerprint("会话", "发送者", "测试明文")

        assertEquals(first, second)
        assertEquals(64, first.length)
        assertNotEquals("测试明文", first)
    }

    @Test
    fun concurrentFirstUseOfKeystoreKeysIsSerialized() = runBlocking<Unit> {
        val suffix = UUID.randomUUID().toString()
        val secretBox = SecretBox("DingLater.TestMessageKey.$suffix")
        val fingerprints = FingerprintService("DingLater.TestFingerprintKey.$suffix")

        try {
            coroutineScope {
                (0 until 24).map { index ->
                    async(Dispatchers.Default) {
                        val plaintext = "并发消息-$index"
                        assertEquals(plaintext, secretBox.decrypt(secretBox.encrypt(plaintext)))
                        assertEquals(64, fingerprints.fingerprint("同一通知", plaintext).length)
                    }
                }.awaitAll()
            }
        } finally {
            secretBox.reset()
            fingerprints.reset()
        }
    }

    @Test
    fun repeatedNotificationKeyStillStoresNewBodiesAndDeduplicatesExactReplay() = runBlocking<Unit> {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val database = Room.inMemoryDatabaseBuilder(context, DingLaterDatabase::class.java).build()
        val suffix = UUID.randomUUID().toString()
        val secretBox = SecretBox("DingLater.TestMessageKey.$suffix")
        val fingerprints = FingerprintService("DingLater.TestFingerprintKey.$suffix")
        val repository = MessageRepository(database.messages(), secretBox, fingerprints)
        val base = CapturedMessage(
            capturedAt = Instant.parse("2026-08-07T02:00:00Z"),
            messageAt = Instant.parse("2026-08-07T01:59:59Z"),
            conversation = "连续通知测试",
            sender = "测试发送者",
            visibleBody = "第一条",
            quality = PayloadQuality.VISIBLE_TEXT,
            sourcePackage = "com.alibaba.android.rimet",
            sourceIdentity = "same-status-bar-notification-key",
            dingTalkVersion = "test",
            conversationIsGroup = false
        )

        try {
            assertTrue(repository.append(base, 7).inserted)
            assertTrue(repository.append(base.copy(visibleBody = "第二条"), 7).inserted)
            assertFalse(repository.append(base, 7).inserted)
        } finally {
            database.close()
            secretBox.reset()
            fingerprints.reset()
        }
    }

    @Test
    fun repositoryRoundTripKeepsProtectedFieldsOutOfDatabaseBytes() = runBlocking<Unit> {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val databaseName = "encrypted-storage-test.db"
        context.deleteDatabase(databaseName)
        val database = Room.databaseBuilder(context, DingLaterDatabase::class.java, databaseName).build()
        val repository = MessageRepository(database.messages(), SecretBox(), FingerprintService())
        val protectedValues = listOf(
            "验收会话-7a31",
            "验收发送者-8b42",
            "验收正文-9c53🙂\n第二行",
            "验收来源-a064"
        )
        val captured = CapturedMessage(
            capturedAt = Instant.parse("2026-08-06T05:00:00Z"),
            messageAt = Instant.parse("2026-08-06T04:59:59Z"),
            conversation = protectedValues[0],
            sender = protectedValues[1],
            visibleBody = protectedValues[2],
            quality = PayloadQuality.BIG_TEXT,
            sourcePackage = "com.alibaba.android.rimet",
            sourceIdentity = protectedValues[3],
            dingTalkVersion = "test",
            conversationIsGroup = true
        )

        val append = repository.append(captured, retentionDays = 7)
        val roundTrip = append.message?.let { repository.get(it.id) }
        assertNotNull(roundTrip)
        assertEquals(protectedValues[0], roundTrip?.captured?.conversation)
        assertEquals(protectedValues[1], roundTrip?.captured?.sender)
        assertEquals(protectedValues[2], roundTrip?.captured?.visibleBody)
        assertEquals(protectedValues[3], roundTrip?.captured?.sourceIdentity)
        database.close()

        val databasePath = context.getDatabasePath(databaseName)
        val storedBytes = buildList {
            listOf(databasePath, java.io.File("${databasePath.path}-wal")).forEach { file ->
                if (file.exists()) add(file.readBytes())
            }
        }.fold(byteArrayOf()) { all, bytes -> all + bytes }
        protectedValues.forEach { plaintext ->
            assertFalse(
                "Protected value appeared in Room database bytes",
                storedBytes.containsSequence(plaintext.toByteArray(StandardCharsets.UTF_8))
            )
        }
        context.deleteDatabase(databaseName)
    }

    private fun ByteArray.containsSequence(needle: ByteArray): Boolean {
        if (needle.isEmpty() || size < needle.size) return false
        outer@ for (start in 0..size - needle.size) {
            for (offset in needle.indices) {
                if (this[start + offset] != needle[offset]) continue@outer
            }
            return true
        }
        return false
    }
}
