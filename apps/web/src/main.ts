import { provideHttpClient } from "@angular/common/http";
import { APP_BASE_HREF } from "@angular/common";
import { bootstrapApplication } from "@angular/platform-browser";
import { provideRouter, withInMemoryScrolling } from "@angular/router";
import { AppComponent } from "./app.component";
import { routes } from "./app.routes";
import { ViewportScroller } from "@angular/common";
import { WorkspaceViewportScroller } from "./workbench/workspace-viewport-scroller";

bootstrapApplication(AppComponent, {
  providers: [
    { provide: APP_BASE_HREF, useValue: "/" },
    { provide: ViewportScroller, useClass: WorkspaceViewportScroller },
    provideHttpClient(),
    provideRouter(
      routes,
      withInMemoryScrolling({ scrollPositionRestoration: "enabled" }),
    ),
  ],
}).catch((error: unknown) => {
  console.error("braid could not start", error);
});
