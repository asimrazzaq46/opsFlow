import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Shell } from './layout/shell';

@Component({
  selector: 'app-root',
  imports: [Shell],
  template: '<app-shell />',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
