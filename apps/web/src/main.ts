import { provideHttpClient } from "@angular/common/http";
import { APP_BASE_HREF } from "@angular/common";
import { bootstrapApplication } from "@angular/platform-browser";
import { provideRouter, withInMemoryScrolling } from "@angular/router";
import { AppComponent } from "./app.component";
import { routes } from "./app.routes";

bootstrapApplication(AppComponent, {
  providers: [
    { provide: APP_BASE_HREF, useValue: "/" },
    provideHttpClient(),
    provideRouter(
      routes,
      withInMemoryScrolling({ scrollPositionRestoration: "enabled" }),
    ),
  ],
}).catch((error: unknown) => {
  console.error("Weft could not start", error);
});
