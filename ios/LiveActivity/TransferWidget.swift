import SwiftUI
import WidgetKit
import ActivityKit

@main struct RobinShareLiveWidgets: WidgetBundle {
    var body: some Widget { TransferWidget() }
}

struct TransferWidget: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: TransferActivityAttributes.self) { context in
            VStack(alignment: .leading, spacing: 10) {
                Label(context.attributes.direction + ": " + context.attributes.filename, systemImage: "arrow.up.arrow.down.circle.fill")
                    .font(.headline).lineLimit(1)
                ProgressView(value: context.state.fraction)
                    .tint(.indigo)
                HStack {
                    Text(context.state.message)
                    Spacer()
                    Text(context.state.fraction, format: .percent.precision(.fractionLength(0)))
                }.font(.caption).foregroundStyle(.secondary)
            }
            .padding(16)
            .activityBackgroundTint(.indigo.opacity(0.12))
            .activitySystemActionForegroundColor(.indigo)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Label(context.attributes.direction, systemImage: "arrow.up.arrow.down")
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(context.state.fraction, format: .percent.precision(.fractionLength(0)))
                }
                DynamicIslandExpandedRegion(.bottom) {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(context.attributes.filename).lineLimit(1)
                        ProgressView(value: context.state.fraction).tint(.indigo)
                    }
                }
            } compactLeading: {
                Image(systemName: "arrow.up.arrow.down")
            } compactTrailing: {
                Text(context.state.fraction, format: .percent.precision(.fractionLength(0)))
                    .monospacedDigit().font(.caption2)
            } minimal: {
                Image(systemName: "arrow.up.arrow.down")
            }
        }
    }
}
