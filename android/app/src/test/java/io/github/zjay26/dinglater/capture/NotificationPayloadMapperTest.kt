package io.github.zjay26.dinglater.capture

import io.github.zjay26.dinglater.model.PayloadQuality
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class NotificationPayloadMapperTest {
    private val mapper = NotificationPayloadMapper()
    private val observedAt = Instant.parse("2026-08-06T03:00:00Z")

    @Test
    fun `messaging style wins and preserves individual messages`() {
        val result = mapper.map(
            payload(
                messages = listOf(
                    RawMessagingMessage("第一条", "小林", 1_000),
                    RawMessagingMessage("第二条", "小周", 2_000)
                ),
                bigText = "不应采用",
                text = "也不应采用"
            ),
            observedAt
        )

        assertEquals(PayloadQuality.MESSAGING_STYLE, result.quality)
        assertEquals(listOf("第一条", "第二条"), result.messages.map { it.visibleBody })
        assertEquals(listOf("小林", "小周"), result.messages.map { it.sender })
        assertEquals("项目群", result.messages.first().conversation)
        assertEquals(2, result.sourceMessageCount)
    }

    @Test
    fun `big text wins over lines and visible text`() {
        val result = mapper.map(
            payload(bigText = "完整展开正文", textLines = listOf("摘要一", "摘要二"), text = "单行"),
            observedAt
        )

        assertEquals(PayloadQuality.BIG_TEXT, result.quality)
        assertEquals("完整展开正文", result.messages.single().visibleBody)
        assertTrue(!result.messages.single().quality.potentiallyIncomplete)
    }

    @Test
    fun `text lines are joined and marked potentially incomplete`() {
        val result = mapper.map(payload(textLines = listOf(" 第一行 ", "第二行")), observedAt)

        assertEquals(PayloadQuality.TEXT_LINES, result.quality)
        assertEquals("第一行\n第二行", result.messages.single().visibleBody)
        assertTrue(result.messages.single().quality.potentiallyIncomplete)
    }

    @Test
    fun `empty messaging entries fall back to big text`() {
        val result = mapper.map(
            payload(
                messages = listOf(RawMessagingMessage("   ", "", 1_000)),
                bigText = "可用正文"
            ),
            observedAt
        )

        assertEquals(PayloadQuality.BIG_TEXT, result.quality)
        assertEquals("可用正文", result.messages.single().visibleBody)
        assertEquals(1, result.emptyBodyCount)
    }

    @Test
    fun `group summary and empty payloads are ignored`() {
        assertTrue(mapper.map(payload(isGroupSummary = true), observedAt).messages.isEmpty())
        val empty = mapper.map(payload(), observedAt)
        assertTrue(empty.messages.isEmpty())
        assertEquals(1, empty.emptyBodyCount)
    }

    private fun payload(
        messages: List<RawMessagingMessage> = emptyList(),
        bigText: String = "",
        textLines: List<String> = emptyList(),
        text: String = "",
        isGroupSummary: Boolean = false
    ) = RawNotificationPayload(
        sourcePackage = "com.alibaba.android.rimet",
        sourceIdentity = "0|com.alibaba.android.rimet|42",
        postTime = 1_700_000_000_000,
        title = "小林",
        text = text,
        bigText = bigText,
        textLines = textLines,
        messages = messages,
        conversationTitle = "项目群",
        isGroupConversation = true,
        isGroupSummary = isGroupSummary,
        dingTalkVersion = "8.0.0 (8000000)"
    )
}
