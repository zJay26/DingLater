package io.github.zjay26.dinglater

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performScrollTo
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MainActivityComposeTest {
    @get:Rule
    val composeRule = createAndroidComposeRule<MainActivity>()

    @Test
    fun onboardingExplainsLocalCaptureAndHistoryBoundary() {
        composeRule.onNodeWithText("DingLater").assertIsDisplayed()
        composeRule.onNodeWithText("只在本机保存").assertIsDisplayed()
        composeRule.onNodeWithText("内容可能不完整").assertIsDisplayed()
        composeRule.onNodeWithText("只会记录完成此步骤后新出现的钉钉通知，不导入已有历史。")
            .performScrollTo()
            .assertIsDisplayed()
    }
}
