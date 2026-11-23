import { useTranslation } from 'react-i18next';
import DropdownSelect from '../DropdownSelect';
import { SUPPORTED_LANGUAGES, setLanguage, type Language } from '../../i18n';

export default function LanguageSection() {
  const { t, i18n } = useTranslation('settings');

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <h2 className="text-xl font-semibold mb-4">{t('language.title')}</h2>
      <div className="form-control max-w-xs">
        <DropdownSelect
          items={SUPPORTED_LANGUAGES.map((code) => ({
            value: code,
            // Language names stay in their own script so users can find their language even
            // when the current UI language is not theirs.
            label: code === 'zh-CN' ? t('language.zhCN') : t('language.en'),
          }))}
          value={i18n.language}
          onChange={(value) => setLanguage(value as Language)}
        />
      </div>
      <p className="text-xs text-base-content/60 mt-3">{t('language.description')}</p>
    </div>
  );
}
