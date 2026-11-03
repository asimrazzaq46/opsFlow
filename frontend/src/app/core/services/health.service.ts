import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { of, timer } from 'rxjs';
import { catchError, map, switchMap, timeout } from 'rxjs/operators';

export type HealthState = 'checking' | 'online' | 'offline';

interface HealthResponse {
  status: string;
  timestamp: string;
}

const POLL_INTERVAL_MS = 30_000;
// A hung API must surface as unavailable instead of keeping the previous answer until the next poll completes.
const CHECK_TIMEOUT_MS = 8_000;

@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly http = inject(HttpClient);
  private readonly _state = signal<HealthState>('checking');
  readonly state = this._state.asReadonly();

  constructor() {
    timer(0, POLL_INTERVAL_MS)
      .pipe(
        switchMap(() =>
          this.http.get<HealthResponse>('/api/v1/health').pipe(
            timeout(CHECK_TIMEOUT_MS),
            map((r): HealthState => (r.status === 'Healthy' ? 'online' : 'offline')),
            catchError(() => of<HealthState>('offline')),
          ),
        ),
      )
      .subscribe((s) => this._state.set(s));
  }
}
