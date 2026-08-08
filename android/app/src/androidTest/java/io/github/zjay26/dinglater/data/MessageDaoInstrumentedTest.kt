package io.github.zjay26.dinglater.data

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MessageDaoInstrumentedTest {
    private lateinit var database: DingLaterDatabase
    private lateinit var dao: MessageDao

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        database = Room.inMemoryDatabaseBuilder(context, DingLaterDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        dao = database.messages()
    }

    @After
    fun tearDown() = database.close()

    @Test
    fun stateTransitionsRetentionCleanupAndDuplicateProtection() = runBlocking {
        assertTrue(dao.insert(entity("one", "fingerprint-one", state = "INBOX", expiresAt = 200)) >= 0)
        assertEquals(-1L, dao.insert(entity("duplicate", "fingerprint-one", state = "INBOX", expiresAt = 300)))

        dao.updateState("one", "SNOOZED", 150, 120)
        assertEquals("SNOOZED", dao.get("one")?.state)
        assertEquals(1, dao.releaseDue(150))
        assertEquals("INBOX", dao.get("one")?.state)

        dao.updateRetention(50)
        assertEquals(150L, dao.get("one")?.expiresAt)
        assertEquals(1, dao.deleteExpired(150))
        assertTrue(dao.observeAll().first().isEmpty())
    }

    private fun entity(id: String, fingerprint: String, state: String, expiresAt: Long) = MessageEntity(
        id = id,
        fingerprint = fingerprint,
        capturedAt = 100,
        messageAt = 100,
        expiresAt = expiresAt,
        updatedAt = 100,
        state = state,
        snoozedUntil = null,
        quality = "BIG_TEXT",
        sourcePackage = "com.alibaba.android.rimet",
        sourceIdentity = byteArrayOf(1),
        dingTalkVersion = "test",
        conversationIsGroup = false,
        conversation = byteArrayOf(2),
        sender = byteArrayOf(3),
        body = byteArrayOf(4)
    )
}
