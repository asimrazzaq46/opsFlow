import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { provideTranslateHttpLoader } from '@ngx-translate/http-loader';

import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { routes } from './app.routes';
import { DEFAULT_LANGUAGE } from './i18n/language.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withFetch(), withInterceptors([authInterceptor])), // Bearer + 401 refresh
    provideTranslateService({ fallbackLang: DEFAULT_LANGUAGE }),
    provideTranslateHttpLoader({ prefix: '/i18n/', suffix: '.json' }),
    // Bootstrap and the first navigation wait until a stored session is validated, so guards never see a half-restored state.
    provideAppInitializer(() => inject(AuthService).restore()),
  ],
};
