import CoreImage.CIFilterBuiltins
import SwiftUI
import UIKit

struct ShareQRCard: View {
    let link: URL
    private var qrImage: UIImage? {
        let generator = CIFilter.qrCodeGenerator()
        generator.message = Data(link.absoluteString.utf8)
        generator.correctionLevel = "M"
        guard let output = generator.outputImage else { return nil }
        let ciContext = CIContext()
        guard let cgImage = ciContext.createCGImage(output.transformed(by: CGAffineTransform(scaleX: 8, y: 8)),
                                                    from: output.extent.applying(CGAffineTransform(scaleX: 8, y: 8))) else { return nil }
        return UIImage(cgImage: cgImage)
    }
    var body: some View {
        VStack(alignment: .center, spacing: 12) {
            if let qrImage {
                Image(uiImage: qrImage)
                    .interpolation(.none)
                    .resizable()
                    .scaledToFit()
                    .frame(maxWidth: 200)
                    .padding(16)
                    .background(.white, in: RoundedRectangle(cornerRadius: 16))
                    .accessibilityLabel("QR-Code zum Empfang der Datei im Browser")
            }
            Text("Ohne App herunterladen")
                .font(.subheadline.bold())
            Text("QR-Code scannen oder Link öffnen. Der Empfänger benötigt nur einen Browser.")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            ShareLink(item: link) {
                Label("Link teilen", systemImage: "square.and.arrow.up")
            }.buttonStyle(.glass)
        }
        .frame(maxWidth: .infinity)
    }
}
