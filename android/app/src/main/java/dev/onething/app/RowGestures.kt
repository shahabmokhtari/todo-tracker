package dev.onething.app

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.hoverable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsHoveredAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerEventType
import androidx.compose.ui.input.pointer.isSecondaryPressed
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp

/**
 * A row's touch, mouse and keyboard: a tap (or Enter) opens it, touch and hold or a right-click opens its menu, and a
 * mouse pointing at it shades it ([hover]).
 */
@OptIn(ExperimentalFoundationApi::class)
fun Modifier.rowGestures(onClick: () -> Unit, onMenu: () -> Unit, hover: Boolean = true): Modifier = composed {
    val interaction = remember { MutableInteractionSource() }
    val hovered by interaction.collectIsHoveredAsState()
    val menu by rememberUpdatedState(onMenu)
    this
        .pointerInput(Unit) {
            // A right-click opens the menu (before the click below sees it).
            awaitPointerEventScope {
                while (true) {
                    val event = awaitPointerEvent(PointerEventPass.Initial)
                    if (event.type == PointerEventType.Press && event.buttons.isSecondaryPressed) {
                        event.changes.forEach { it.consume() }
                        menu()
                    }
                }
            }
        }
        .then(if (hover) Modifier.hoverable(interaction).background(if (hovered) MaterialTheme.colorScheme.onSurface.copy(alpha = 0.04f) else Color.Transparent) else Modifier)
        .combinedClickable(interactionSource = interaction, indication = androidx.compose.material3.ripple(), onLongClick = onMenu, onClick = onClick)
}

/**
 * Swipe a row right to mark it done ([onDone]; null: it can't be, it has subtasks to do) or left for Later
 * ([onLater]). The row springs back: what happens next (Undo, the Later menu) shows on its own.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SwipeActions(onDone: (() -> Unit)?, onLater: () -> Unit, content: @Composable () -> Unit) {
    val done by rememberUpdatedState(onDone)
    val later by rememberUpdatedState(onLater)
    val state = rememberSwipeToDismissBoxState(
        confirmValueChange = { value ->
            when (value) {
                SwipeToDismissBoxValue.StartToEnd -> done?.invoke()
                SwipeToDismissBoxValue.EndToStart -> later()
                SwipeToDismissBoxValue.Settled -> Unit
            }
            // Never dismissed: the list redraws (done) or the menu opens (Later).
            false
        },
        positionalThreshold = { it * 0.35f },
    )
    SwipeToDismissBox(
        state = state,
        enableDismissFromStartToEnd = onDone != null,
        backgroundContent = {
            val direction = state.dismissDirection
            val toDone = direction == SwipeToDismissBoxValue.StartToEnd
            val color = when (direction) {
                SwipeToDismissBoxValue.StartToEnd -> ViewColors.done
                SwipeToDismissBoxValue.EndToStart -> Brand.violet
                SwipeToDismissBoxValue.Settled -> Color.Transparent
            }
            Box(
                Modifier.fillMaxSize().clip(RoundedCornerShape(16.dp)).background(color).padding(horizontal = 20.dp),
                contentAlignment = if (toDone) Alignment.CenterStart else Alignment.CenterEnd,
            ) {
                if (direction != SwipeToDismissBoxValue.Settled) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Icon(if (toDone) Icons.Filled.Check else ViewIcons.later, contentDescription = null, tint = Color.White, modifier = Modifier.size(20.dp))
                        Text(if (toDone) "Mark done" else "Later", color = Color.White, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold)
                    }
                }
            }
        },
    ) { content() }
}
