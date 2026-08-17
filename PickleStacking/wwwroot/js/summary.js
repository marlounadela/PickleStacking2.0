// Summary download helpers for PickleStacking

window.pickleStacking = window.pickleStacking || {};

window.pickleStacking.downloadPdf = function (content) {
    try {
        // Build a printable HTML document
        const html = `
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8" />
<title>PickleStacking Game Summary</title>
<style>
    body { font-family: 'Segoe UI', Arial, sans-serif; margin: 40px; color: #1c2733; }
    h1 { color: #2e7d32; font-size: 28px; margin-bottom: 4px; }
    h2 { color: #2e7d32; font-size: 18px; border-bottom: 2px solid #2e7d32; padding-bottom: 6px; margin-top: 28px; }
    .subtitle { color: #6b7a8c; font-size: 14px; margin-bottom: 24px; }
    .session-info { display: grid; grid-template-columns: 1fr 1fr; gap: 8px 24px; margin-bottom: 8px; }
    .session-info div { padding: 4px 0; }
    .label { font-weight: 700; color: #6b7a8c; font-size: 12px; text-transform: uppercase; letter-spacing: 0.05em; }
    .value { font-size: 15px; font-weight: 600; }
    table { width: 100%; border-collapse: collapse; margin-top: 12px; }
    th { text-align: left; padding: 10px 12px; background: #f4f6f8; border-bottom: 2px solid #e3e8ee; font-size: 12px; text-transform: uppercase; letter-spacing: 0.05em; color: #6b7a8c; }
    td { padding: 10px 12px; border-bottom: 1px solid #e3e8ee; font-size: 14px; }
    tr:nth-child(even) td { background: #fafbfc; }
    .award { margin: 8px 0; padding: 10px 14px; background: #f4f6f8; border-radius: 8px; font-size: 15px; }
    .award .emoji { font-size: 20px; margin-right: 8px; }
    .award .title { font-weight: 700; }
    .award .player { font-weight: 600; color: #2e7d32; }
    .footer { margin-top: 40px; text-align: center; color: #6b7a8c; font-size: 12px; border-top: 1px solid #e3e8ee; padding-top: 16px; }
    .divider { border: none; border-top: 1px solid #e3e8ee; margin: 24px 0; }
</style>
</head>
<body>
${content}
</body>
</html>`;

        // Open a new window and print to PDF
        const win = window.open('', '_blank');
        if (!win) {
            alert('Please allow pop-ups to download the PDF.');
            return;
        }
        win.document.write(html);
        win.document.close();
        win.focus();
        setTimeout(() => {
            win.print();
        }, 300);
    } catch (e) {
        console.error('PDF download failed:', e);
    }
};

window.pickleStacking.downloadWord = function (content) {
    try {
        // Build a Word-compatible HTML document
        const html = `
<html xmlns:o="urn:schemas-microsoft-com:office:office"
      xmlns:w="urn:schemas-microsoft-com:office:word"
      xmlns="http://www.w3.org/TR/REC-html40">
<head>
<meta charset="utf-8" />
<title>PickleStacking Game Summary</title>
<!--[if gte mso 9]>
<xml>
<w:WordDocument>
<w:View>Print</w:View>
<w:Zoom>100</w:Zoom>
</w:WordDocument>
</xml>
<![endif]-->
<style>
    body { font-family: 'Segoe UI', Arial, sans-serif; margin: 40px; color: #1c2733; }
    h1 { color: #2e7d32; font-size: 28px; margin-bottom: 4px; }
    h2 { color: #2e7d32; font-size: 18px; border-bottom: 2px solid #2e7d32; padding-bottom: 6px; margin-top: 28px; }
    .subtitle { color: #6b7a8c; font-size: 14px; margin-bottom: 24px; }
    .session-info { margin-bottom: 8px; }
    .session-info div { padding: 4px 0; }
    .label { font-weight: 700; color: #6b7a8c; font-size: 12px; text-transform: uppercase; letter-spacing: 0.05em; }
    .value { font-size: 15px; font-weight: 600; }
    table { width: 100%; border-collapse: collapse; margin-top: 12px; }
    th { text-align: left; padding: 10px 12px; background: #f4f6f8; border-bottom: 2px solid #e3e8ee; font-size: 12px; text-transform: uppercase; letter-spacing: 0.05em; color: #6b7a8c; }
    td { padding: 10px 12px; border-bottom: 1px solid #e3e8ee; font-size: 14px; }
    tr:nth-child(even) td { background: #fafbfc; }
    .award { margin: 8px 0; padding: 10px 14px; background: #f4f6f8; border-radius: 8px; font-size: 15px; }
    .award .emoji { font-size: 20px; margin-right: 8px; }
    .award .title { font-weight: 700; }
    .award .player { font-weight: 600; color: #2e7d32; }
    .footer { margin-top: 40px; text-align: center; color: #6b7a8c; font-size: 12px; border-top: 1px solid #e3e8ee; padding-top: 16px; }
    .divider { border: none; border-top: 1px solid #e3e8ee; margin: 24px 0; }
</style>
</head>
<body>
${content}
</body>
</html>`;

        const blob = new Blob(['\ufeff', html], { type: 'application/msword' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'PickleStacking_Game_Summary.doc';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    } catch (e) {
        console.error('Word download failed:', e);
    }
};

window.pickleStacking.printSummary = function () {
    try {
        window.print();
    } catch (e) {
        console.error('Print failed:', e);
    }
};