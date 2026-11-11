namespace Synect.Mcp.Tools;

/// <summary>
/// Uzbek example questions (Latin and Cyrillic script) per tool, appended to the tool descriptions in tools/list by
/// <see cref="Hosting.OrganizationParameter"/>. The words follow docs/glossary.md (hisob-faktura / ҳисоб-фактура,
/// dalolatnoma / далолатнома, ko'chirma / кўчирма, qoldiq / қолдиқ, tushum / тушум, imzo / имзо ...). DRAFT: written as
/// hypotheses; to be checked by native speakers and replaced by real questions of accountants.
/// </summary>
public static class ToolExamples
{
    public static readonly IReadOnlyDictionary<string, string> Uzbek = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Sources and portfolio
        ["get_data_freshness"] = "«Ma'lumotlar qachon yangilangan?», «Bank ko'chirmasi oxirgi marta qachon yuklandi?», «Маълумотлар қачон янгиланган?»",
        ["request_data_refresh"] = "«Bank ma'lumotlarini yangila», «Didoxdan yangi hujjatlarni yukla», «Банк маълумотларини қайта юкла»",
        // Money
        ["get_account_balances"] = "«Hisob raqamlardagi qoldiq qancha?», «Bugungi qoldiq», «Ҳисоб рақамлардаги қолдиқ қанча?»",
        ["get_balance_history"] = "«Oktabr oyida qoldiq qanday o'zgardi?», «Октябрь ойида қолдиқ қандай ўзгарди?»",
        ["get_balance_stats"] = "«O'rtacha qoldiq qancha bo'lgan?», «Энг кам қолдиқ қачон бўлган?»",
        ["get_cash_flow"] = "«Oktabrda tushum va chiqim qancha?», «Pul oqimi oylar bo'yicha», «Октябрда тушум ва чиқим қанча?»",
        ["get_largest_transactions"] = "«Eng katta to'lovlar qaysilar?», «Энг катта тўловлар қайсилар?»",
        ["get_transactions_for_day"] = "«Kecha qanday to'lovlar bo'ldi?», «5-noyabrdagi operatsiyalar», «Кеча қандай тўловлар бўлди?»",
        ["find_idle_accounts"] = "«Qaysi hisob raqamlarda harakat yo'q?», «Қайси ҳисоб рақамларда ҳаракат йўқ?»",
        ["check_balance_integrity"] = "«Ko'chirmada qoldiqlar to'g'ri keladimi?», «Кўчирмада қолдиқлар тўғри келадими?»",
        // Counterparties
        ["get_counterparty_payments"] = "«Romashka MChJga qancha to'ladik?», «Ромашка МЧЖга қанча тўладик?»",
        ["get_top_counterparties"] = "«Eng katta mijozlarimiz kimlar?», «Eng ko'p to'lov qilgan kontragentlar», «Энг кўп тўлов қилган контрагентлар»",
        ["get_counterparty_summary"] = "«Qurilish servis bilan hisob-kitob qanday?», «Қурилиш сервис билан ҳисоб-китоб қандай?»",
        ["compare_counterparty_periods"] = "«Bu chorakda mijozlar bo'yicha o'tgan chorak bilan solishtir», «Бу чоракни ўтган чорак билан солиштир»",
        ["get_counterparty_changes"] = "«Qaysi yangi kontragentlar paydo bo'ldi?», «Қайси контрагентлар йўқолди?»",
        ["get_payments_without_documents"] = "«Hujjatsiz to'lovlar bormi?», «Ҳужжатсиз тўловлар борми?»",
        ["get_counterparty_concentration"] = "«Tushumning qancha qismi bitta mijozdan?», «Тушумнинг қанча қисми битта мижоздан?»",
        ["find_counterparties"] = "«Romashka MChJ INN qanday?», «Ромашка МЧЖ ИНН қандай?»",
        // Documents
        ["get_documents_awaiting_our_signature"] = "«Qaysi hujjatlar imzomizni kutmoqda?», «Imzolanmagan hisob-fakturalar», «Қайси ҳужжатлар имзомизни кутмоқда?»",
        ["get_documents_awaiting_partner_signature"] = "«Kontragent qaysi hujjatlarni imzolamadi?», «Контрагент қайси ҳужжатларни имзоламади?»",
        ["get_document_counts"] = "«Oktabrda nechta hisob-faktura bor?», «Октябрда нечта ҳисоб-фактура бор?»",
        ["get_rejected_documents"] = "«Rad etilgan hujjatlar bormi?», «Рад этилган ҳисоб-фактуралар», «Рад этилган ҳужжатлар борми?»",
        ["search_documents"] = "«15-sonli hisob-fakturani top», «7-sonli shartnoma bo'yicha hujjatlar», «15-сонли ҳисоб-фактурани топ»",
        ["get_largest_documents"] = "«Eng katta hisob-fakturalar qaysilar?», «Энг катта ҳисоб-фактуралар қайсилар?»",
        ["get_vat_summary"] = "«Oktabr uchun QQS qancha?», «Октябрь учун ҚҚС қанча?»",
        // Reconciliation
        ["get_paid_documents"] = "«Qaysi hisob-fakturalar to'langan?», «Қайси ҳисоб-фактуралар тўланган?»",
        ["get_unpaid_documents"] = "«To'lanmagan hisob-fakturalar», «Qaysi dalolatnomalar to'lanmagan?», «Тўланмаган ҳисоб-фактуралар»",
        ["get_partially_paid_documents"] = "«Qisman to'langan hisob-fakturalar», «Қисман тўланган ҳисоб-фактуралар»",
        ["get_unmatched_payments"] = "«Hujjat bilan bog'lanmagan tushumlar», «Қайси тушумлар ҳужжатга боғланмаган?»",
        ["get_signed_unpaid_acts"] = "«Imzolangan, lekin to'lanmagan dalolatnomalar», «Имзоланган, лекин тўланмаган далолатномалар»",
        ["get_multi_document_payments"] = "«Bir nechta hisob-faktura uchun bitta to'lov», «Бир нечта ҳисоб-фактура учун битта тўлов»",
        ["get_amount_differences"] = "«Summasi hisob-fakturadan farq qiladigan to'lovlar», «Суммаси фарқ қиладиган тўловлар»",
        ["get_payments_without_reference"] = "«To'lov maqsadida hujjat raqami yo'q to'lovlar», «Тўлов мақсадида ҳужжат рақами йўқ тўловлар»",
        ["get_payment_delay_stats"] = "«Mijozlar o'rtacha necha kunda to'laydi?», «Мижозлар ўртача неча кунда тўлайди?»",
        ["get_account_mismatches"] = "«Boshqa hisob raqamga qilingan to'lovlar», «Бошқа ҳисоб рақамга қилинган тўловлар»",
        ["get_possible_duplicate_payments"] = "«Ikki marta to'langan bo'lishi mumkin bo'lgan to'lovlar», «Икки марта тўланган тўловлар борми?»",
        ["get_counterparty_balance_estimate"] = "«Debitorlik va kreditorlik qarzi qancha?», «Romashka MChJ bizga qancha qarz?», «Дебиторлик қарзи қанча?»",
        ["get_match_details"] = "«Bu moslik nima asosida?», «Бу мослик қайси сабабларга асосланган?»",
        ["confirm_match"] = "«Bu moslikni tasdiqla», «Бу мосликни тасдиқла»",
        ["reject_match"] = "«Bu moslik noto'g'ri, rad et», «Бу мослик нотўғри, рад эт»",
        // Attention
        ["get_attention_items"] = "«Nimalarga e'tibor berish kerak?», «Muhim ogohlantirishlar ro'yxati», «Нималарга эътибор бериш керак?», «Қандай огоҳлантиришлар бор?»",
        ["get_attention_summary"] = "«Muhim ogohlantirishlar bormi?», «Муҳим огоҳлантиришлар борми?»",
        ["update_attention_item"] = "«Ko'rib chiqildi deb belgila», «Бир ҳафтадан кейин эслат»",
        // Portfolio
        ["list_organizations"] = "«Qaysi tashkilotlar ulangan?», «Қайси ташкилотлар уланган?»",
        ["get_portfolio_overview"] = "«Barcha mijozlar bo'yicha holat qanday?», «Барча мижозлар бўйича ҳолат қандай?»",
        ["get_connections_overview"] = "«Qaysi ulanishlar ishlamayapti?», «Қайси уланишлар ишламаяпти?»",
    };

    /// <summary>The sentence appended to a tool description, or empty when the tool has no examples yet.</summary>
    public static string For(string tool) =>
        Uzbek.TryGetValue(tool, out var examples) ? " Uzbek examples (Latin and Cyrillic; draft wording to be checked): " + examples + "." : "";
}
